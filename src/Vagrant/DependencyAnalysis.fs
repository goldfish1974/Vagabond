﻿module internal Nessos.Vagrant.DependencyAnalysis

    open System
    open System.Collections
    open System.Collections.Generic
    open System.Runtime.Serialization
    open System.Reflection

    open Microsoft.FSharp.Quotations
    open Microsoft.FSharp.Quotations.Patterns
    open Microsoft.FSharp.Quotations.DerivedPatterns
    open Microsoft.FSharp.Quotations.ExprShape

    open Mono.Cecil
    open Mono.Reflection

    open Nessos.Vagrant.Utils

    open FsPickler

    /// gathers all types that occur in an object graph

    type Dependencies = (Assembly * seq<Type>) list

    let gatherObjectDependencies (obj:obj) : Dependencies =
        let gathered = new HashSet<Type>()
        let tracker = new ObjectTracker()
        let inline add t = gathered.Add t |> ignore
        
        let rec traverseObj (obj : obj) =
            match obj with
            | null -> ()
            | :? Type as t -> traverseType t
            | :? MemberInfo as m -> traverseType m.DeclaringType
            | :? Delegate as d -> 
                traverseType <| d.GetType()
                traverseType d.Method.DeclaringType
                traverseObj d.Target
                for d' in d.GetInvocationList() do
                    if d <> d' then traverseObj d'
            | _ ->
                if tracker.IsFirstOccurence obj then
                    let t = obj.GetType()
                    traverseType t

                    if t.IsPrimitive || t = typeof<string> then ()
                    elif t.IsArray then
                        let et = t.GetElementType()
                        if et.IsPrimitive then ()
                        else
                            for e in obj :?> Array do
                                traverseObj e
                    else
                        let fields = t.GetFields(BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)

                        for f in fields |> Array.filter (fun f -> not f.FieldType.IsPrimitive) do
                            let o = f.GetValue(obj)
                            traverseObj o

        and traverseType (t : Type) =   
            if t.IsArray || t.IsByRef || t.IsPointer then
                traverseType <| t.GetElementType()
            elif t.IsGenericType && not t.IsGenericTypeDefinition then
                add <| t.GetGenericTypeDefinition()
                for ta in t.GetGenericArguments() do
                    traverseType ta
            else
                add t

        do traverseObj obj
        gathered 
        |> Seq.groupBy (fun t -> t.Assembly)
        |> Seq.toList

    let getLoadedAssemblies () =
        System.AppDomain.CurrentDomain.GetAssemblies()
        |> Seq.map (fun a -> a.FullName, a)
        |> Map.ofSeq

    let isSystemAssembly =
        let getPublicKey (a : Assembly) = a.GetName().GetPublicKey()
        let systemPkt = getPublicKey typeof<int>.Assembly
        fun (a:Assembly) -> getPublicKey a = systemPkt

    // recursively traverse assembly dependency graph
    let traverseDependencies (assemblies : seq<Assembly>) =

        let loadedAssemblies = getLoadedAssemblies ()

        let tryResolveLoadedAssembly (an : AssemblyName) =
            loadedAssemblies.TryFind an.FullName
            |> Option.filter (not << isSystemAssembly)

        let rec traverseDependencyGraph (graph : Map<string, Assembly * Assembly list>) (remaining : Assembly list) =
            match remaining with
            | [] -> graph |> Map.toList |> List.map snd
            | a :: tail when graph.ContainsKey a.FullName || isSystemAssembly a -> traverseDependencyGraph graph tail
            | a :: tail -> 
                let dependencies = a.GetReferencedAssemblies() |> Array.choose tryResolveLoadedAssembly |> Array.toList
                traverseDependencyGraph (graph.Add(a.FullName, (a, dependencies))) (dependencies @ tail)

        assemblies
        |> Seq.toList
        |> traverseDependencyGraph Map.empty
        |> getTopologicalOrdering


    let parseDynamicAssemblies (state : GlobalDynamicAssemblyState) (assemblies : seq<Assembly>) =

        let domainAssemblies = getLoadedAssemblies()
        let getReferencedAssemblies (def : AssemblyDefinition) =
            def.Modules 
            |> Seq.collect(fun modl -> modl.AssemblyReferences |> Seq.map (fun ar -> domainAssemblies.[ar.FullName]))
            |> Seq.toList

        let isDynamicAssemblyRequiringCompilation (a : Assembly) =
            if a.IsDynamic then
                state.DynamicAssemblies.TryFind a.FullName 
                |> Option.forall (fun info -> info.HasFreshTypes)
            else
                false

        let rec traverse (graph : Map<string, Assembly * AssemblyDefinition * Assembly list>) (remaining : Assembly list) =
            match remaining with
            | [] -> graph
            | a :: rest when graph.ContainsKey a.FullName || not <| isDynamicAssemblyRequiringCompilation a -> traverse graph rest
            | a :: rest ->
                // parse dynamic assembly
                let snapshot = AssemblySaver.Read a

                // Assembly.GetReferencedAssemblies is unreliable with dynamic assemblies;
                // Use Cecil to correctly resolve dependencies
                let assemblyReferences = getReferencedAssemblies snapshot

                let graph' = graph.Add(a.FullName, (a, snapshot, assemblyReferences))
                
                traverse graph' (rest @ assemblyReferences)


        let dynamicAssemblies = traverse Map.empty <| Seq.toList assemblies

        dynamicAssemblies 
        |> Map.toList
        |> List.map (fun (_,(a,_,deps)) -> (a, deps |> List.filter (fun a -> dynamicAssemblies.ContainsKey a.FullName)))
        |> getTopologicalOrdering
        |> List.map (fun a -> dynamicAssemblies.[a.FullName])

    let getDynamicDependenciesRequiringCompilation (state : GlobalDynamicAssemblyState) (dependencies : Dependencies) =
        dependencies
        |> List.filter(fun (a,types) ->
            if a.IsDynamic then
                match state.DynamicAssemblies.TryFind a.FullName with
                | Some info -> types |> Seq.forall(fun t -> info.TypeIndex.ContainsKey t.FullName) |> not
                | None -> true
            else
                false)
        |> List.map fst


    let remapDependencies (state : GlobalDynamicAssemblyState) (dependencies : Dependencies) =
        let rec remap (a : Assembly, ts : seq<Type>) =
            if a.IsDynamic then
                match state.DynamicAssemblies.TryFind a.FullName with
                | None -> failwithf "Vagrant: no slices have been created for assembly '%s'." a.FullName
                | Some info ->
                    seq {
                        yield! info.AssemblyReferences |> Seq.collect (fun a -> remap (a,[]))

                        for t in ts do
                            match info.TypeIndex.TryFind t.FullName with 
                            | None -> failwithf "Vagrant: no slice has been created for dynamic type '%O'." t
                            | Some i -> yield i.Assembly
                    }

            else Seq.singleton a

        dependencies |> Seq.collect remap |> traverseDependencies