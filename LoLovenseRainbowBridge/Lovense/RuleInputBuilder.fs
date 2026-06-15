namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge.Scoring

type RuleInputBuilder(?cache: IAppCache) =

    let cacheVariables now =
        match cache with
        | None -> Map.empty
        | Some cache ->
            cache.Read()
            |> AppCache.projectAnnotated (Some now)

    new (_scoringConfig: ScoringConfig) = RuleInputBuilder(?cache = None)

    new (_scoringConfig: ScoringConfig, cache: IAppCache) = RuleInputBuilder(?cache = Some cache)

    interface IRuleInputBuilder with
        member _.Build _ input _ =
            cacheVariables input.Now
