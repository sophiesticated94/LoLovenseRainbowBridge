namespace LoLovenseRainbowBridge.Lovense

open System.Globalization

module LovenseFormatting =
    let invariantFloat (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let escapeJsonString (value: string) =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")
