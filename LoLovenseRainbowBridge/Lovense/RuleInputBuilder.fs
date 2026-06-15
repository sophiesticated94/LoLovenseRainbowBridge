namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge.Scoring

type RuleInputBuilder(?cache: IAppCache) =

    let cacheVariables now =
        match cache with
        | None -> Map.empty
        | Some cache ->
            let snapshot = cache.Read()
            let snapshotType = snapshot.GetType()

            let projectChild name =
                let property = snapshotType.GetProperty(name)

                if isNull property then
                    Map.empty
                else
                    property.GetValue(snapshot)
                    |> AppCache.projectAnnotated (Some now)

            [
                projectChild "League"
                projectChild "LeagueRules"
                projectChild "RuleClock"
                projectChild "Ocr"
                projectChild "Lovense"
                projectChild "Toys"
                projectChild "RuntimeContext"
                projectChild "CommandBuilder"
            ]
            |> List.fold RuleInternals.mergeVariables Map.empty

    new (_scoringConfig: ScoringConfig) = RuleInputBuilder(?cache = None)

    new (_scoringConfig: ScoringConfig, cache: IAppCache) = RuleInputBuilder(?cache = Some cache)

    interface IRuleInputBuilder with
        member _.Build _ input _ =
            cacheVariables input.Now
