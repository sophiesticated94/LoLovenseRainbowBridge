namespace LoLovenseRainbowBridge.PositionMapping

open System
open LoLovenseRainbowBridge.MinimapDetector
open LoLovenseRainbowBridge

type MappingMode =
    | Quadrant
    | Continuous
    | ZoneBased
    | Combined

type GameZone =
    | TopLane
    | MidLane
    | BotLane
    | JungleTop
    | JungleBot
    | River
    | Base
    | Unknown

type RotationResult =
    {
        RotationValue: int
        MappingMethod: string
        Zone: GameZone
    }

module PositionMapping =
    let private getQuadrant (position: PlayerPosition) =
        if position.NormalizedX < 0.5 && position.NormalizedY < 0.5 then "TopLeft"
        elif position.NormalizedX >= 0.5 && position.NormalizedY < 0.5 then "TopRight"
        elif position.NormalizedX < 0.5 && position.NormalizedY >= 0.5 then "BottomLeft"
        else "BottomRight"

    let private quadrantToRotation (quadrant: string) (sensitivity: float) =
        match quadrant with
        | "TopLeft" -> int (5.0 * sensitivity)
        | "TopRight" -> int (10.0 * sensitivity)
        | "BottomLeft" -> int (15.0 * sensitivity)
        | "BottomRight" -> int (20.0 * sensitivity)
        | _ -> 10

    let private continuousMapping (position: PlayerPosition) (sensitivity: float) =
        let xRotation = position.NormalizedX * 10.0 * sensitivity
        let yRotation = position.NormalizedY * 10.0 * sensitivity
        int (xRotation + yRotation) |> Shared.clamp 0 20

    let private getZone (position: PlayerPosition) =
        let x = position.NormalizedX
        let y = position.NormalizedY
        
        if x < 0.2 || x > 0.8 then
            if y < 0.3 then TopLane
            elif y > 0.7 then BotLane
            else JungleTop
        elif y < 0.2 || y > 0.8 then
            if x < 0.5 then JungleTop
            else JungleBot
        elif y > 0.3 && y < 0.7 then
            River
        elif y < 0.2 then
            Base
        else
            MidLane

    let private zoneToRotation (zone: GameZone) (sensitivity: float) =
        match zone with
        | TopLane -> int (8.0 * sensitivity)
        | MidLane -> int (10.0 * sensitivity)
        | BotLane -> int (12.0 * sensitivity)
        | JungleTop -> int (15.0 * sensitivity)
        | JungleBot -> int (15.0 * sensitivity)
        | River -> int (18.0 * sensitivity)
        | Base -> int (5.0 * sensitivity)
        | Unknown -> 10

    let private combinedMapping (position: PlayerPosition) (sensitivity: float) =
        let quadrant = getQuadrant position
        let zone = getZone position
        let quadrantRot = quadrantToRotation quadrant sensitivity
        let zoneRot = zoneToRotation zone sensitivity
        let continuousRot = continuousMapping position sensitivity
        
        let avgRot = (float quadrantRot + float zoneRot + float continuousRot) / 3.0
        int avgRot |> Shared.clamp 0 20

    let mapPositionToRotation (position: PlayerPosition) (mode: MappingMode) (sensitivity: float) =
        let zone = getZone position
        
        let rotationValue, mappingMethod =
            match mode with
            | Quadrant ->
                let quadrant = getQuadrant position
                quadrantToRotation quadrant sensitivity, "Quadrant"
            | Continuous ->
                continuousMapping position sensitivity, "Continuous"
            | ZoneBased ->
                zoneToRotation zone sensitivity, "ZoneBased"
            | Combined ->
                combinedMapping position sensitivity, "Combined"
        
        {
            RotationValue = rotationValue
            MappingMethod = mappingMethod
            Zone = zone
        }

    let parseMappingMode (modeString: string) =
        match modeString.ToLower() with
        | "quadrant" -> Some Quadrant
        | "continuous" -> Some Continuous
        | "zonebased" -> Some ZoneBased
        | "combined" -> Some Combined
        | _ -> None
