namespace FsGepa
open System
(*
Compound AI System Optimization. Given Φ, let ΠΦ = ⟨π1, . . . , π|M|⟩ denote the collection of all module
prompts and ΘΦ = ⟨θ1, . . . , θ|M|⟩ the set of module weights. The learnable parameters are thus ⟨Π, Θ⟩Φ. For a
task instance (x, m)—where x maps to the input schema X and m contains evaluator metadata (e.g., gold answers,
evaluation rubrics, code unit tests)—the system induces an output y = Φ(x;⟨Π, Θ⟩Φ). A metric µ : Y × M → [0, 1]
then measures the output quality of y with respect to metadata m (for example by calculating, exact match, F1, pass
rate, etc.). The optimization problem is thus defined by:
*)

type Config = {
    budget : int
    miniBatch : int
    totalFeedbackSize : int
    reflect_merge_split : float
    max_attempts_merge_pair : int
    max_attempts_find_pair : int
}

type Prompt = {
    text : string
}

type Model = {
    id : string
}

///Captures incidental information (e.g. thought traces) produced besides the main output during flow execution
type ExecutionTrace = {moduleId:string; trace:string}

///Captures incidental information produce during the evaluation of an input-output pair
type Feedback = Feedback of string

///<summary>
/// Prompt + model combination.<br />
/// Also specifies the type of input expected and output produced.<br />
/// In the simplest case, the input and output can be of type string
/// </summary>
type GeModule<'input, 'output> = {
    moduleId : string 
    prompt : Prompt
    model : Model    
    inputType: 'input
    outputType: 'output
}

type Eval = {
    score : float
    feedback : Feedback
    traces : ExecutionTrace list
}

type GeTask<'input,'output> = {
    input : 'input
    eval : Config -> 'output * ExecutionTrace list -> Async<Eval>
   }

///<summary>
/// Candidate 'system' (phi) with modules and control flow.<br />
/// Flow processing may interweave tool calls or other non-module invocations.
///</summary>
type GeSystem<'input,'output> = {
    modules : Map<string,GeModule<obj,obj>>
    flow : Config -> Map<string,GeModule<obj,obj>> -> 'input -> Async<'output * ExecutionTrace list>
}

