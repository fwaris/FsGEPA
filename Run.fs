namespace FsGepa.Run
open FSharp.Control
open FsGepa

type GrTask<'a,'b> = {
    task : GeTask<'a,'b>
    evalCache : Eval
}

type GrSystem<'a,'b> = {
    id : string
    parent : string option
    sys : GeSystem<'a,'b>
    avgScore : float
}

type GrRun<'a,'b> = {
    count : int
    candidates : GrSystem<'a, 'b> list
}

module Run =

    let runTask cfg sys ((i,task):int*_) =  
        async {
            let! o = sys.flow cfg sys.modules task.input
            return (i,task),o
        }

    let evalTask cfg ((i,t),o) = 
        async {
            let! e = t.eval cfg o
            return i,e
        }

    let score cfg sys tasks = 
        tasks 
        |> AsyncSeq.ofSeq
        |> AsyncSeq.mapAsync (runTask cfg sys)
        |> AsyncSeq.mapAsync (evalTask cfg)

    let filterEval mid (i,eval) = 
        { eval with 
            traces = 
                eval.traces 
                |> List.filter (fun t -> t.moduleId = mid)
        }

    let filterEvals cfg moduleId = List.map (filterEval moduleId)

    let setPrompt grSys m prompt =
        {grSys with 
            id = Utils.newId()
            parent = Some grSys.id
            sys = {grSys.sys with 
                    modules = 
                        grSys.sys.modules 
                        |> Map.map(fun k v -> 
                            if k = m.moduleId then {v with prompt=prompt} else v)  
                  }
        }

    let scoreCandidates cfg candidates tasksPareto=
        candidates
        |> AsyncSeq.ofSeq
        |> AsyncSeq.map (fun grsys -> 
            let scores = 
                score cfg grsys.sys tasksPareto 
                |> AsyncSeq.toBlockingSeq
                |> Seq.toList
            grsys,scores
        )
        |> AsyncSeq.toBlockingSeq
        |> Seq.toList

    let isDominated (ts:list<int * (Set<float * Set<string>>)>) sid = 
        ts 
        |> List.forall (fun (_,ps) -> (snd Set.maxElement ps).Contains 

            > 
        )        
        ts 
        |> Seq.collect (fun (_,s) -> s |> Seq.collect snd)        
        |> Seq.tryFind (isDominated ts)

    let findDominated (ts:list<int * (Set<float * Set<string>>)>) = 
        ts 
        |> Seq.collect (fun (_,s) -> s |> Seq.collect snd)        
        |> Seq.tryFind (isDominated ts)

    let removeSys (ts:list<int * (Set<float * Set<string>>)>) sid = 
        ts 
        |> List.map(fun (i,(prioritySet)) -> 
            i,
            prioritySet |> Set.map (fun (scr,xs) -> scr, xs |> Set.remove sid))

    let selectCandidate cfg grun tasksPareto = async {
        let scores = scoreCandidates cfg grun.candidates tasksPareto
        let scores = scores |> List.map(fun (s,xs) -> s.id, xs |> List.map(fun (i,e) -> i,e.score))
        let byTask = 
            scores 
            |> List.collect (fun (s,xs) -> xs |> List.map (fun y -> s,y))
            |> List.groupBy (fun (sid,(i,scr)) -> i)
            |> List.map (fun (i,xs) -> 
                let prioritySet = 
                    xs 
                    |> List.groupBy (fun (_,(_,scr)) -> scr) 
                    |> List.map (fun (s,xs) -> s, xs |> List.map fst |> set)
                    |> set                
                i,prioritySet)
        let rec loop ts = 
            match findDominated ts with 
            | Some sid ->
                let ts' = removeSys ts sid
                loop ts'
            | None -> return ts
            loop ts
        let pruned = loop byTask

        



        return grun.candidates.[0]
    }

    let selectModule cfg grSys = grSys.sys.modules |> Map.toList |> List.map snd |> Utils.randSelect 

    ///single step of reflective prompt optimizer process
    let stepReflective cfg grun tasksPareto tasksMB = async {
        let! grSys = selectCandidate cfg grun tasksPareto
        let m = selectModule cfg grSys
        let evals = score cfg grSys.sys tasksMB
                    |> AsyncSeq.toBlockingSeq
                    |> Seq.toList
        let avgScore = evals |> List.averageBy (fun (i,x) -> x.score)
        let mEvals = filterEvals cfg m.moduleId evals
        let! prompt = Llm.Llm.updatePrompt cfg m.prompt mEvals
        let grSys' = setPrompt grSys m prompt
        let evals' = score cfg grSys'.sys tasksMB
                     |> AsyncSeq.toBlockingSeq
                     |> Seq.toList
        let avgScore' = evals' |> List.averageBy (fun (i,x) -> x.score)
        let grun' = 
            if avgScore' >= avgScore then 
                {grun with candidates = grSys' :: grun.candidates}
            else 
                grun
        return {grun' with count=grun'.count+1}
    }

    let sample cfg tasks = 
        let frac = float cfg.miniBatch / float cfg.totalFeedbackSize
        tasks |> Seq.filter(fun _ -> Utils.rng.NextDouble() <= frac) |> Seq.toList

    let run cfg geSys tasksPareto tasksFeedback =
        let grun = {count=0; candidates= [geSys]}
        let rec loop grun = async {
            if grun.count < cfg.budget then 
                let tasksMB = tasksFeedback |> sample cfg
                return! stepReflective cfg grun tasksPareto tasksMB
            else
                return grun
        }
        loop grun
