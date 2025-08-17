namespace FsGEPA
(*
Compound AI System Optimization. Given Φ, let ΠΦ = ⟨π1, . . . , π|M|⟩ denote the collection of all module
prompts and ΘΦ = ⟨θ1, . . . , θ|M|⟩ the set of module weights. The learnable parameters are thus ⟨Π, Θ⟩Φ. For a
task instance (x, m)—where x maps to the input schema X and m contains evaluator metadata (e.g., gold answers,
evaluation rubrics, code unit tests)—the system induces an output y = Φ(x;⟨Π, Θ⟩Φ). A metric µ : Y × M → [0, 1]
then measures the output quality of y with respect to metadata m (for example by calculating, exact match, F1, pass
rate, etc.). The optimization problem is thus defined by:
*)

type Prompt = {
    text : string
}

type Model = {
    id : string
}

type EvaluationTrace = EvaluationTrace of string
type ExecutionTrace = ExecutionTrace of string

type Input = Input of string
type Output = Output of string

type GeTask = {
    input : Input
    Eval : Input * Output -> Async<float*EvaluationTrace option>

}

type GeModule = {
    moduleId : string 
    prompt : Prompt
    model : Model
}

type GeSystem = {
    modules : GeModule list
    Flow : Input * GeModule list -> Async<Output*ExecutionTrace option>
}
