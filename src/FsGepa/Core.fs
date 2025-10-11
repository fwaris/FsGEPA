namespace rec FsGepa
open System
open System.Threading.Channels


///Interface to call an llm - implementation to be supplied by optimization invoker
type IGenerate =
    abstract member generate : model:Model -> systemMessage:string option -> messages:GenMessage list -> responseFormat:Type option -> Async<GenerateResponse>
type GenerateResponse = {output:string; thoughts:string option}
type GenMessage = {role:string; content:string}

type Prompt = {
    text : string
}

type Model = {
    id : string
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
    ///The model that will be used to update the meta prompt
    default_model: Model
    ///Implementation of IGenerate interface - used to run meta prompt for module prompt updates
    generate : IGenerate

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
                            default_model = default_model
                            generate = generate
                            max_sample_input_prompt_length = 2000
                        }

///Events that relay information from the running optimization process
type Telemetry = 
    | NewBest of Map<string,string> * float
    | Frontier of (string*float) list
    | AddReflective of {|score:float; parentScore:float|}
    | AddMerge of {|score:float; parentScore:float|}
    
///Captures the raw LLM input, response and any reasoning/thoughts
type ExecutionTrace = {moduleId:string; inputPrompt:string; response: string; reasoning:string option}

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
