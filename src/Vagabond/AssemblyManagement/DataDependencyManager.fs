﻿module internal Nessos.Vagabond.DataDependencyManagement

open System
open System.Collections.Generic
open System.Reflection
open System.IO
open System.IO.Compression

open Nessos.FsPickler
open Nessos.FsPickler.Hashing

open Nessos.Vagabond.SliceCompilerTypes
open Nessos.Vagabond.AssemblyNaming
open Nessos.Vagabond.AssemblyCache
open Nessos.Vagabond.AssemblyManagementTypes

/// threshold in bytes after which to persist binding to file
let persistThreshold = 10L * 1024L
/// Use Gzip compression for large persisted data bindings
let compress = true

/// pickle value to file
let picklePersistedBinding (state : VagabondState) (path : string) (value : obj) =
    use fs = File.OpenWrite path
    let stream =
        if compress then new GZipStream(fs, CompressionLevel.Optimal) :> Stream
        else fs :> _
    state.Serializer.Serialize(stream, value)

/// unpickle value from file
let unpicklePersistedBinding (state : VagabondState) (path : string) =
    use fs = File.OpenRead path
    let stream =
        if compress then new GZipStream(fs, CompressionMode.Decompress) :> Stream
        else fs :> _
    state.Serializer.Deserialize<obj>(stream)

/// export data dependency for locally generated slice
let exportDataDependency (state : VagabondState) (assemblyPath : string) 
                            (id : DataDependencyId) (current : DataExportState option) (field : FieldInfo) : DataExportState =
    // get current value for static field
    let value = field.GetValue(null)
    // compute the MurMur3 hash for the object; this will allow detection of future mutations of the dependency
    // it also records any exceptions in case of the object being non-serializable.
    let hashResult = try state.Serializer.ComputeHash value |> Choice1Of2 with e -> Choice2Of2 e
    // decide if a new pickle generation needs to be generated
    // compare with previously recorded hash, if it exists.
    let requireNewGen =
        match current, hashResult with
        | None, _ -> true // no previous generation, update
        | Some { Hash = Choice1Of2 hash }, Choice1Of2 hash' -> hash <> hash' // only update if different hash, i.e. value updated
        | Some { Hash = Choice2Of2 _ }, Choice1Of2 _ -> true // if was exception but now successful, update
        | Some { Hash = Choice1Of2 _ }, Choice2Of2 _ -> false // if was successful but is exception, do not update
        | Some { Hash = Choice2Of2 e }, Choice2Of2 e' -> e.GetType() <> e'.GetType() // if was and is exception, only update if different exns

    // no update required, return current state
    if not requireNewGen then Option.get current else

    let data =
        match hashResult with
        // file size exceeds threshold; declare persisted to file
        | Choice1Of2 hash when hash.Length > persistThreshold -> Persisted hash.Length
        // pickle in metadata
        | Choice1Of2 hash -> let pickle = state.Serializer.PickleTyped value in Pickled pickle
        // pickle serialization exception
        | Choice2Of2 e -> Errored (e.ToString(), state.Serializer.PickleTyped e)

    let dependencyInfo =
        match current with
        | None -> { Id = id ; Name = field.ToString() ; Generation = 0 ; FieldInfo = state.Serializer.PickleTyped field ; Data = data }
        | Some { Info = di } -> { di with Generation = di.Generation + 1 ; Data = data }

    // persist to file, if so required
    let persistFile =
        match data with
        | Persisted _ ->
            let persistedPath = AssemblyCache.GetPersistedDataPath(assemblyPath, id, dependencyInfo.Generation)
            picklePersistedBinding state persistedPath value
            Some (id, persistedPath)
        | _ -> None

    { AssemblyPath = assemblyPath ; Hash = hashResult ; Info = dependencyInfo ; PersistFile = persistFile }

/// export data dependencies given controller state for provided compiled dynamic assembly slice
let exportDataDependencies (state : VagabondState) (slice : DynamicAssemblySlice) =
    let assemblyPath = slice.Assembly.Location
    let exportState =
        match state.DataExportState.TryFind assemblyPath with
        | None -> slice.StaticFields |> Array.mapi (fun id fI -> exportDataDependency state assemblyPath id None fI)
        | Some deps -> slice.StaticFields |> Array.mapi (fun id fI -> exportDataDependency state assemblyPath id (Some deps.[id]) fI)

    let persisted = exportState |> Array.choose (fun de -> de.PersistFile)
    let dependencies = exportState |> Array.map (fun de -> de.Info)
    let va = VagabondAssembly.CreateManaged(slice.Assembly, true, dependencies, persisted)
    { state with DataExportState = state.DataExportState.Add(assemblyPath, exportState) }, va


/// import data dependency for locally loaded assembly slice
let importDataDependency (state : VagabondState) (va : VagabondAssembly) 
                            (current : DataGeneration option) (persistPath : string option) (di : DataDependencyInfo) =

    // determine if data dependency requires updating
    let shouldUpdate =
        match current with
        | None -> true
        | Some gen -> gen < di.Generation

    // return current generation if update not required
    if not shouldUpdate then Option.get current else

    let updateValue (value : obj) =
        let field = state.Serializer.UnPickleTyped di.FieldInfo
        field.SetValue(null, value)

    match di.Data, persistPath with
    // unpickle from metadata
    | Pickled pickle, _ ->
        let value = state.Serializer.UnPickleTyped pickle
        updateValue value
    | Persisted _, None -> invalidOp <| sprintf "Assembly '%O' missing data initializer for value '%s'." va.FullName di.Name
    // unpickle from file
    | Persisted _, Some path ->
        let value = unpicklePersistedBinding state path
        updateValue value
    | Errored _, _ -> ()

    di.Generation

/// import data dependencies to local AppDomain
let importDataDependencies (state : VagabondState) (va : VagabondAssembly) =
    if Array.isEmpty va.Metadata.DataDependencies then state else

    let persisted = va.PersistedDataDependencies |> Map.ofArray
    let importState =
        match state.DataImportState.TryFind va.Image with
        | None -> va.Metadata.DataDependencies |> Array.map (fun dd -> importDataDependency state va None (persisted.TryFind dd.Id) dd)
        | Some gens -> va.Metadata.DataDependencies |> Array.map (fun dd -> importDataDependency state va (Some gens.[dd.Id]) (persisted.TryFind dd.Id) dd)

    { state with DataImportState = state.DataImportState.Add(va.Image, importState) }