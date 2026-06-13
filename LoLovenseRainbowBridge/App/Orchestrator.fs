namespace LoLovenseRainbowBridge.App

open System.Threading
open System.Threading.Tasks

type IAppJob =
    abstract Name: string
    abstract RunAsync: CancellationToken -> Task
