namespace rec FsGepa
open System
open System.Threading.Channels


///Interface to call an llm - implementation to be supplied by optimization invoker
type IGenerate =
    abstract member generate : 
                        model:Model 
                        -> systemMessage:string option 
                        -> messages:GenMessage list 
                        -> responseFormat:Type option 
                        -> options:GenOpts option
                        -> Async<GenerateResponse>
type GenerateResponse = {output:string; thoughts:string option}
type GenMessage = {role:string; content:string}
type GenOpts = {temperature:float32 option; max_tokens:int option}
    with static member Default = {temperature=None; max_tokens=None}

type Prompt = {
    text : string
}

type Model = {
    id : string
}

///Optimization loop implementation.
type OptimizerMode =
    | GepaMode
    | VistaMode

///Configuration options for the VISTA optimizer mode.
type VistaConfig = {
    ///Maximum number of root-cause hypotheses to request per reflective step.
    hypothesis_count : int
    ///Maximum number of hypotheses to rewrite and validate on the minibatch.
    hypotheses_to_validate : int
    ///Probability of exploring a lower-priority hypothesis during selection.
    epsilon_greedy : float
    ///After this many rejected proposals, VISTA may restart from the seed/frontier.
    random_restart_stagnation : int option
    ///Probability of taking a restart once the stagnation threshold is met.
    random_restart_probability : float
    ///When restarting, probability of branching from the original seed candidate.
    restart_from_seed_probability : float
    ///Optional override model used for diagnosis and prompt rewriting.
    reflector_model : Model option
    ///Optional model overrides used during minibatch validation.
    validation_models : Model list
    ///When true, VISTA may still use GEPA merge proposals.
    use_merge : bool
}
    with static member Default = {
                            hypothesis_count = 4
                            hypotheses_to_validate = 3
                            epsilon_greedy = 0.20
                            random_restart_stagnation = Some 4
                            random_restart_probability = 0.35
                            restart_from_seed_probability = 0.60
                            reflector_model = None
                            validation_models = []
                            use_merge = false
                        }

///Labeled root-cause hypothesis produced during VISTA diagnosis.
type Hypothesis = {
    id : string
    label : string
    summary : string
    evidence : string list
    priority : int
    confidence : float
}

///Describes how a candidate was produced.
type CandidateOrigin =
    | Seed
    | ReflectiveUpdate of moduleId:string
    | MergeUpdate
    | VistaUpdate of moduleId:string * hypothesisId:string * label:string * restarted:bool

///Interpretable trace item for a single optimization step.
type OptimizationTraceEntry = {
    step : int
    action : string
    parentId : string option
    moduleId : string option
    hypothesisId : string option
    hypothesisLabel : string option
    hypothesisSummary : string option
    evidence : string list
    parentScore : float option
    candidateScore : float option
    accepted : bool option
    restartReason : string option
    notes : string option
}

///Optimization parameters 
type Config = {
    ///Number of runs ('roll outs')
    budget : int
    ///Number of parallel flow executions
    flow_parallelism : int 
    ///Size of the mini batch sample over which a potential new candidate is scored
    mini_batch_size : int
    ///Total number of tasks in the 'feedback' tasks set from which the mini batch is drawn
    feedback_tasks_count : int
    ///The fraction [0.1 .. 0.99] which favors reflection-derived new candidate vs merge-derived one
    reflect_merge_split : float
    ///Number of attempts to find a suitable parent for merging two candidates before giving up
    max_attempts_find_merge_parent : int
    ///Number of attempts to find a suitable pair of candidates to merge
    max_attempts_find_pair : int
    ///If set, Telemetry messages will be posted to the channel when the optimization is running
    telemetry_channel : Channel<Telemetry> option
    ///Optimizer implementation to run.
    optimizer_mode : OptimizerMode
    ///Structured diagnosis and restart controls used by VISTA mode.
    vista : VistaConfig
    ///The model that will be used to update the meta prompt
    default_model: Model
    ///Implementation of IGenerate interface - used to run meta prompt for module prompt updates
    generator : IGenerate

    ///<summary>
    /// Maximum allowed length for each task input prompt used in reflective update.<br />
    /// This can be used to control the meta prompt size.<br />
    /// The input prompt is simply truncated to the allowed length.
    ///<summary> 
    max_sample_input_prompt_length : int
}
    with static member CreateDefault generate feedback_task_count default_model =
                        {
                            budget = 20
                            flow_parallelism = 5
                            mini_batch_size = 10
                            feedback_tasks_count = feedback_task_count
                            reflect_merge_split = 0.7
                            max_attempts_find_merge_parent = 10
                            max_attempts_find_pair = 10
                            telemetry_channel = None
                            optimizer_mode = GepaMode
                            vista = VistaConfig.Default
                            default_model = default_model
                            generator = generate
                            max_sample_input_prompt_length = 2000
                        }

///Events that relay information from the running optimization process
type Telemetry = 
    | NewBest of Map<string,string> * float
    | Frontier of (string*float) list
    | AddReflective of {|score:float; parentScore:float|}
    | AddMerge of {|score:float; parentScore:float|}
    | GeneratedPrompt of string
    | HypothesesGenerated of {|parentId:string; moduleId:string; hypotheses:(string * string) list|}
    | HypothesisValidated of {|parentId:string; moduleId:string; hypothesisId:string; label:string; score:float; parentScore:float|}
    | RestartTriggered of {|sourceId:string; reason:string|}
    | CandidateAccepted of OptimizationTraceEntry
    | CandidateRejected of OptimizationTraceEntry
    
///Captures the raw LLM input, response and any reasoning/thoughts
type ExecutionTrace = {moduleId:string; taskInput:string; response: string; reasoning:string option}

///Captures any feedback from the evaluation of a flow input-output pair
type Feedback = Feedback of string
    with member this.text() = match this with Feedback s -> s

///<summary>
/// Prompt + model combination.<br />
/// Also specifies the type of input expected and output produced.<br />
/// In the simplest case, the input and output can be of type string
/// </summary>
type GeModule = {
    moduleId : string 
    prompt : Prompt
    model : Model option //default 
    inputSchema: string option
    outputSchema: string option
    metaPrompt : ModuleMetaPrompt option
} 
    with static member Default = {
                                    moduleId = "no id"
                                    prompt = {text="no prompt"}
                                    model = None
                                    inputSchema = None
                                    outputSchema = None
                                    metaPrompt = None
                                 }

///Meta prompt to update a module prompt. This will override the generic system meta prompt.
type ModuleMetaPrompt = 
    {
        metaPrompt : string 
        validate : string -> Async<string option>
    }

///Result of an evaluation of a flow.
type Eval = {
    score : float
    feedback : Feedback
}

///Represents the input data for a task - along with how to evaluate the output from the flow result
type GeTask<'input,'output> = {
    input : 'input
    evaluate : Config -> 'input -> FlowResult<'output> -> Async<Eval> //TODO: Reconsider. Is eval specific to a task?
   }

///Output of the end-to-end execution of the flow
type FlowResult<'output> = {
    output : 'output
    traces : ExecutionTrace list
}

///Contains all relevant data, for a single task, required to update a module prompt
type EvaledTask<'input,'output> = {
    index : int
    task : GeTask<'input,'output>
    flowResult : FlowResult<'output>
    eval : Eval
}

///Type signature of the Flow function 
type Flow<'input,'output> = Config -> Map<string,GeModule> -> 'input -> Async<FlowResult<'output>> 

///<summary>
/// Candidate 'system' with modules and control flow.<br />
/// Flow processing may interweave tool calls or other non-module invocations.
///</summary>
type GeSystem<'input,'output> = {    
    modules : Map<string,GeModule>
    flow : Flow<'input,'output>
}
