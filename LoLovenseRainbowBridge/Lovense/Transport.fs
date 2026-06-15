namespace LoLovenseRainbowBridge.Lovense

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open SocketIOClient

type LovenseHttpResponse =
    {
        CorrelationId: string
        Url: string
        StatusCode: int
        Body: string
    }

module Transport =

    let private requestCanceledResult (url: string) (message: string) (ct: CancellationToken) =
        if ct.IsCancellationRequested then
            None
        else
            Some(SocketUrlRequestFailed(url, message))

    let newCorrelationId () =
        Guid.NewGuid().ToString("N")

    let postJsonAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (correlationId: string)
        (url: string)
        (redactedRequestBody: string)
        (requestBody: string)
        (ct: CancellationToken)
        =
        task {
            logger.RawLovenseSocketHttp(correlationId, url, "request", None, redactedRequestBody)

            try
                use request = new HttpRequestMessage(HttpMethod.Post, url)
                request.Content <- new StringContent(requestBody, Encoding.UTF8, Constants.Lovense.JsonMediaType)

                let! response = http.SendAsync(request, ct)
                let! responseBody = response.Content.ReadAsStringAsync(ct)
                let statusCode = int response.StatusCode

                logger.RawLovenseSocketHttp(correlationId, url, "response", Some statusCode, responseBody)

                if not response.IsSuccessStatusCode then
                    return Error(SocketUrlRequestFailed(url, $"HTTP {statusCode}: {responseBody}"))
                else
                    return
                        Ok
                            {
                                CorrelationId = correlationId
                                Url = url
                                StatusCode = statusCode
                                Body = responseBody
                            }
            with
            | :? TaskCanceledException as ex ->
                match requestCanceledResult url $"HTTP request timed out or was canceled: {ex.Message}" ct with
                | Some error -> return Error error
                | None -> return raise (OperationCanceledException())
            | :? OperationCanceledException as ex ->
                match requestCanceledResult url $"HTTP request was canceled: {ex.Message}" ct with
                | Some error -> return Error error
                | None -> return raise (OperationCanceledException())
            | ex ->
                return Error(SocketUrlRequestFailed(url, ex.Message))
        }

    let postJsonWithHeadersAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (correlationId: string)
        (url: string)
        (headers: (string * string) list)
        (redactedRequestBody: string)
        (requestBody: string)
        (ct: CancellationToken)
        =
        task {
            logger.RawLovenseSocketHttp(correlationId, url, "request", None, redactedRequestBody)

            try
                use request = new HttpRequestMessage(HttpMethod.Post, url)
                request.Content <- new StringContent(requestBody, Encoding.UTF8, Constants.Lovense.JsonMediaType)

                for name, value in headers do
                    request.Headers.TryAddWithoutValidation(name, value) |> ignore

                let! response = http.SendAsync(request)
                let! responseBody = response.Content.ReadAsStringAsync(ct)
                let statusCode = int response.StatusCode

                logger.RawLovenseSocketHttp(correlationId, url, "response", Some statusCode, responseBody)

                if not response.IsSuccessStatusCode then
                    return Error(SocketUrlRequestFailed(url, $"HTTP {statusCode}: {responseBody}"))
                else
                    return
                        Ok
                            {
                                CorrelationId = correlationId
                                Url = url
                                StatusCode = statusCode
                                Body = responseBody
                            }
            with
            | :? TaskCanceledException as ex ->
                match requestCanceledResult url $"HTTP request timed out or was canceled: {ex.Message}" ct with
                | Some error -> return Error error
                | None -> return raise (OperationCanceledException())
            | :? OperationCanceledException as ex ->
                match requestCanceledResult url $"HTTP request was canceled: {ex.Message}" ct with
                | Some error -> return Error error
                | None -> return raise (OperationCanceledException())
            | ex ->
                return Error(SocketUrlRequestFailed(url, ex.Message))
        }

    let emitJsonAsync
        (client: SocketIO)
        (logger: StructuredSessionLogger)
        (eventName: string)
        (payload: string)
        (timeoutMs: int)
        (ct: CancellationToken)
        =
        task {
            let correlationId =
                try
                    JsonNode.Parse(payload)
                    |> Json.tryString Constants.Lovense.AckIdField
                    |> Option.toObj
                with _ ->
                    null
                |> fun value -> if String.IsNullOrWhiteSpace value then Guid.NewGuid().ToString("N") else value

            logger.RawLovenseSocketEvent(correlationId, eventName, "emit", payload)

            try
                use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                timeoutCts.CancelAfter(timeoutMs)

                let payloadElement = JsonSerializer.Deserialize<JsonElement>(payload)

                let eventData: obj seq =
                    [ payloadElement :> obj ]

                do! client.EmitAsync(eventName, eventData, timeoutCts.Token)
                return Ok correlationId
            with
            | :? OperationCanceledException when not ct.IsCancellationRequested ->
                return Error(CommandTimeout(eventName, timeoutMs))
            | :? OperationCanceledException ->
                return raise (OperationCanceledException())
            | ex ->
                return Error(CommandEmitFailed(eventName, ex.Message))
        }
