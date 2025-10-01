namespace FsGEPA
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
}


type Prompt = {
    text : string
}

type Model = {
    id : string
}


///Captures incidental information produce during the evaluation of an input-output pair
type EvaluationTrace = EvaluationTrace of string

///Captures incidental information (e.g. thought traces) produced besides the main output during flow execution
type ExecutionTrace = ExecutionTrace of string


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

type GeTask<'input,'output> = {
    input : 'input
    eval : Config -> 'output * ExecutionTrace -> Async<float*EvaluationTrace>
}

///<summary>
/// Control flow: runs input through one or more modules, possibly multiple times.<br />
/// Processing may interweave tool calls or other non-module invocations.
///</summary>
type GeFlow<'input,'output> = {
    modules : Map<string,GeModule<obj,obj>>
    Flow : Config -> Map<string,GeModule<obj,obj>> -> 'input -> Async<'output * ExecutionTrace>
}
