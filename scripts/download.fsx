open System
open System.IO
open System.Net.Http

let home = Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

let (@@) (a:string) (b:string) = Path.Combine(a,b)

let url = @"http://curtis.ml.cmu.edu/datasets/hotpot/hotpot_dev_distractor_v1.json"

let save (url:string) (path:string) = async {
    use cl = new HttpClient()
    use! str = cl.GetStreamAsync(url) |> Async.AwaitTask
    use outstr = File.Create path
    do! str.CopyToAsync(outstr) |> Async.AwaitTask
}

save url (home @@ "Downloads" @@ "hotpot_dev_distractor_v1.json") |> Async.RunSynchronously

