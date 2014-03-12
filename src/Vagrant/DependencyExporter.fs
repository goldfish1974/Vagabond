﻿module internal Nessos.Vagrant.DependencyExporter

    open System
    open System.Reflection

    open Nessos.Vagrant
    open Nessos.Vagrant.Utils

    open FsPickler

    let mkDependencyInfo (pickler : FsPickler) 
                            (state : GlobalDynamicAssemblyState) 
                            (generationIdx : Map<string, int>) 
                            (assembly : Assembly) =

        if assembly.IsDynamic then invalidOp "Dynamic assembly not exportable."

        let an = assembly.GetName()
        match state.TryGetDynamicAssemblyName an.Name with
        | None ->
            let info =
                {
                    Assembly = assembly
            
                    SourceId = Guid.Empty
                    SliceId = 0
                    IsDynamicAssemblySlice = false
                    BlobGeneration = 0
                    TypeInitializationBlobs = []
                    TypeInitializationErrors = []
                    ActualQualifiedName = assembly.FullName
                }

            generationIdx, info

        | Some dynamicAssemblyName ->
            an.Name <- dynamicAssemblyName
            let sliceInfo = 
                state.DynamicAssemblies.[an.FullName].GeneratedSlices 
                |> List.find (fun s -> s.Assembly = assembly)

            let fieldPickles, pickleFailures =
                sliceInfo.StaticFields
                |> List.map(fun (sourceField, targetField) -> 
                    try
                        let value = sourceField.GetValue(null)
                        let pickle = pickler.Pickle<obj>(value)
                        Choice1Of2 (targetField, pickle)
                    with e -> Choice2Of2 (targetField, e.ToString()))
                |> Choice2.partition

            let generation = 1 + defaultArg (generationIdx.TryFind assembly.FullName) 0

            let info =
                {
                    SourceId = state.ServerId
                    SliceId = sliceInfo.SliceId
                    ActualQualifiedName = sliceInfo.DynamicAssemblyName

                    IsDynamicAssemblySlice = true
                    Assembly = sliceInfo.Assembly
                    BlobGeneration = generation
                    TypeInitializationBlobs = fieldPickles
                    TypeInitializationErrors = pickleFailures
                }

            let generationIdx = generationIdx.Add(assembly.FullName, generation)

            generationIdx, info


    let loadDependencyInfo (pickler : FsPickler) (localServerId : Guid option) (loadState : Map<string, int>) (info : DependencyInfo) =
        match localServerId with
        | Some id when id = info.SourceId -> loadState
        | _ ->
            match loadState.TryFind info.Assembly.FullName with
            | Some gen when gen >= info.BlobGeneration -> loadState
            | _ ->
                for fI, blob in info.TypeInitializationBlobs do
                    let value = pickler.UnPickle<obj>(blob)
                    fI.SetValue(null, value)

                loadState.Add(info.Assembly.FullName, info.BlobGeneration)


    let mkDependencyExporter (pickler : FsPickler) (stateF : unit -> GlobalDynamicAssemblyState) =
        mkStatefulWrapper Map.empty (fun state a -> mkDependencyInfo pickler (stateF()) state a)


    type DependencyLoader = StatefulWrapper<Map<string,int>, DependencyInfo, unit>

    let mkDependencyLoader (pickler : FsPickler) (localServerId : Guid option) : DependencyLoader =
        mkStatefulWrapper Map.empty (fun state i -> loadDependencyInfo pickler localServerId state i, ())