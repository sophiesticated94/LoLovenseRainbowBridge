namespace LoLovenseRainbowBridge.Lovense

open System
open System.Globalization
open System.Text.RegularExpressions
open NCalc

type RuleExpressionEvaluator() =
    let powerPattern =
        Regex(@"(\([^()]+\)|[A-Za-z_][A-Za-z0-9_.]*|\d+(?:\.\d+)?)\s*\^\s*(\([^()]+\)|[A-Za-z_][A-Za-z0-9_.]*|\d+(?:\.\d+)?)", RegexOptions.Compiled)

    let normalizePowerOperator expression =
        let rec loop current =
            let next =
                powerPattern.Replace(
                    current,
                    fun (m: Match) ->
                        let left = m.Groups[1].Value
                        let right = m.Groups[2].Value
                        $"Pow({left},{right})"
                )

            if String.Equals(next, current, StringComparison.Ordinal) then next else loop next

        loop expression

    interface IRuleExpressionEvaluator with
        member _.Evaluate expression variables =
            if String.IsNullOrWhiteSpace expression then
                Ok 0.0
            else
                try
                    let e = Expression(normalizePowerOperator expression)

                    for KeyValue(name, value) in variables do
                        e.Parameters[name] <- value

                    let raw = e.Evaluate()

                    match raw with
                    | :? bool as value -> Ok(if value then 1.0 else 0.0)
                    | :? byte as value -> Ok(float value)
                    | :? int16 as value -> Ok(float value)
                    | :? int as value -> Ok(float value)
                    | :? int64 as value -> Ok(float value)
                    | :? single as value -> Ok(float value)
                    | :? double as value -> Ok value
                    | :? decimal as value -> Ok(float value)
                    | null -> Error "Expression evaluated to null."
                    | value ->
                        match Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, parsed -> Ok parsed
                        | false, _ -> Error $"Expression evaluated to unsupported value '{value}'."
                with ex ->
                    Error ex.Message
