open System
open System.IO
open System.Globalization
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Telegram.Bot
open Telegram.Bot.Polling
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open FSharp.Control.Tasks.V2.ContextInsensitive
open Telegram.Bot.Types.ReplyMarkups
open System.Collections.Generic

let safeTask (f: unit -> Task<'T>) (onError: exn -> 'T) = task {
    try return! f() with ex -> return onError ex
}

let token = Environment.GetEnvironmentVariable "TELEGRAM_TOKEN"
let openAiKey = Environment.GetEnvironmentVariable "OPENAI_API_KEY"
if String.IsNullOrWhiteSpace token then failwith "Missing TELEGRAM_TOKEN!"
if String.IsNullOrWhiteSpace openAiKey then failwith "Missing OPENAI_API_KEY!"

let botClient = TelegramBotClient(token)
let cts = new CancellationTokenSource()
let receiverOptions = ReceiverOptions(AllowedUpdates = [||])

let pendingChoices = Dictionary<int64, int * JsonElement list>()
let pendingChoiceMessages = Dictionary<int64, int>()

type AlphaCall = { Name: string; CA: string; ChainId: string; StartPrice: string; Pair: string; Link: string }
let alphaCalls = ResizeArray<AlphaCall>()

let stateFile = "state.json"
let saveState () =
    try
        let json = JsonSerializer.Serialize(alphaCalls, JsonSerializerOptions(WriteIndented = true))
        File.WriteAllText(stateFile, json)
    with _ -> ()

let loadState () =
    try
        if File.Exists(stateFile) then
            let json = File.ReadAllText(stateFile)
            let doc = JsonDocument.Parse(json)
            alphaCalls.Clear()
            for elem in doc.RootElement.EnumerateArray() do
                let get (name: string) (def: string) =
                    let mutable v = Unchecked.defaultof<JsonElement>
                    if elem.TryGetProperty(name, &v) then
                        match v.ValueKind with
                        | JsonValueKind.String ->
                            let s = v.GetString()
                            if isNull s then def else s
                        | JsonValueKind.Null -> def
                        | _ -> def
                    else def
                let name = get "Name" ""
                let ca = get "CA" ""
                let chainId = get "ChainId" ""
                let startPrice = get "StartPrice" ""
                let pair = get "Pair" ""
                let link = get "Link" ""
                alphaCalls.Add({ Name = name; CA = ca; ChainId = chainId; StartPrice = startPrice; Pair = pair; Link = link })
    with _ -> ()

let printLoadedAlphaCalls () =
    printfn "Loaded alphaCalls from state.json:" 
    for a in alphaCalls do
        printfn "Name: %s | CA: %s | ChainId: %s | StartPrice: %s | Pair: %s | Link: %s" a.Name a.CA a.ChainId a.StartPrice a.Pair a.Link

type AlphaExtractionResult = { CA: string; ChainId: string }

let extractAlphaData (text: string) =
    safeTask
        (fun () ->
            task {
                use client = new HttpClient()
                client.DefaultRequestHeaders.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", openAiKey)
                let prompt =
                    $"""From the following text, extract:
- The blockchain contract address (CA).
- The Dexscreener chainId (ONLY text values like: ethereum, bsc, polygon, avalanche, fantom, arbitrum, optimism, base, etc. DO NOT return any number like '1' or '56').

Return exactly in this format:
CA: <contract address>
CHAINID: <chain id text name>

Text:
{text}"""
                let payload = {| model = "gpt-4.1-nano"; messages = [| {| role = "user"; content = prompt |} |] |}
                let! resp = client.PostAsync(
                    "https://api.openai.com/v1/chat/completions",
                    new StringContent(JsonSerializer.Serialize payload, Encoding.UTF8, "application/json"),
                    cancellationToken = cts.Token)
                resp.EnsureSuccessStatusCode() |> ignore
                let! body = resp.Content.ReadAsStringAsync()
                let doc = JsonDocument.Parse(body)
                let content = doc.RootElement.GetProperty("choices").[0].GetProperty("message").GetProperty("content").GetString()
                let lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                let ca =
                    lines
                    |> Array.tryPick (fun l -> 
                        if l.StartsWith("CA:", StringComparison.OrdinalIgnoreCase) then Some(l.Substring(3).Trim()) else None)
                    |> Option.defaultValue "NONE"
                let chain =
                    lines
                    |> Array.tryPick (fun l -> 
                        if l.StartsWith("CHAINID:", StringComparison.OrdinalIgnoreCase) then Some(l.Substring(8).Trim().ToLowerInvariant()) else None)
                    |> Option.defaultValue "UNKNOWN"
                let finalChain = if Regex.IsMatch(chain, "^\\d+$") then "UNKNOWN" else chain
                return { CA = ca; ChainId = finalChain }
            })
        (fun _ -> { CA = "NONE"; ChainId = "UNKNOWN" })

type TokenInfo = { Name: string; PriceUsd: string }

let getDexscreenerTokenInfo (ca: string) (chainId: string) (pairId: string option) =
    safeTask (fun () -> task {
        use client = new HttpClient()
        if pairId.IsSome then
            let url = sprintf "https://api.dexscreener.com/latest/dex/pairs/%s/%s" chainId pairId.Value
            let! resp = client.GetAsync(url, cancellationToken = cts.Token)
            if resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                let doc = JsonDocument.Parse(body)
                let mutable pairElem = Unchecked.defaultof<JsonElement>
                if doc.RootElement.TryGetProperty("pair", &pairElem) && pairElem.ValueKind = JsonValueKind.Object then
                    let baseToken = pairElem.GetProperty("baseToken")
                    let name = baseToken.GetProperty("name").GetString()
                    let priceProp = pairElem.GetProperty("priceUsd")
                    let price =
                        match priceProp.ValueKind with
                        | JsonValueKind.Number -> priceProp.GetDecimal().ToString(CultureInfo.InvariantCulture)
                        | JsonValueKind.String -> priceProp.GetString()
                        | _ -> "N/A"
                    return Ok { Name = name; PriceUsd = price }
                else
                    return Error "No 'pair' object in response."
            else return Error resp.ReasonPhrase
        else
            let url = sprintf "https://api.dexscreener.com/latest/dex/tokens/%s" ca
            let! resp = client.GetAsync(url, cancellationToken = cts.Token)
            if resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                let doc = JsonDocument.Parse(body)
                let mutable pairsElem = Unchecked.defaultof<JsonElement>
                if doc.RootElement.TryGetProperty("pairs", &pairsElem) && pairsElem.ValueKind = JsonValueKind.Array then
                    let pairs = pairsElem.EnumerateArray()
                    let poolOpt = pairs |> Seq.tryFind (fun p -> 
                        let mutable chainElem = Unchecked.defaultof<JsonElement>
                        p.TryGetProperty("chainId", &chainElem) &&
                        (chainElem.GetString().Equals(chainId, StringComparison.OrdinalIgnoreCase))
                    )
                    match poolOpt with
                    | Some pool ->
                        let baseToken = pool.GetProperty("baseToken")
                        let name = baseToken.GetProperty("name").GetString()
                        let priceProp = pool.GetProperty("priceUsd")
                        let price =
                            match priceProp.ValueKind with
                            | JsonValueKind.Number -> priceProp.GetDecimal().ToString(CultureInfo.InvariantCulture)
                            | JsonValueKind.String -> priceProp.GetString()
                            | _ -> "N/A"
                        return Ok { Name = name; PriceUsd = price }
                    | None -> return Error "No matching pool for given chainId"
                else
                    return Error "No 'pairs' array in response."
            else return Error resp.ReasonPhrase
    }) (fun ex -> Error ex.Message)

let sendMessageAsync (bot: ITelegramBotClient) (chatId: int64) (text: string) (ct: CancellationToken) =
    task {
        try
            do bot.SendMessage(ChatId.op_Implicit(chatId), text, cancellationToken = ct) |> ignore
        with ex ->
            printfn "[SEND ERROR] %s" ex.Message
    }

let handleAddAlpha (bot: ITelegramBotClient) (msg: Message) (args: string) (ct: CancellationToken) = task {
    if String.IsNullOrWhiteSpace args then
        do! sendMessageAsync bot msg.Chat.Id "Usage: /addalphacall <description>" ct
    else
        do! sendMessageAsync bot msg.Chat.Id "🔍 Searching CA…" ct
        let! ext = extractAlphaData args
        if ext.CA = "NONE" then
            do! sendMessageAsync bot msg.Chat.Id "❌ Could not extract a valid contract address." ct
        else
            use client = new HttpClient()
            let url = sprintf "https://api.dexscreener.com/latest/dex/tokens/%s" ext.CA
            let! resp = client.GetAsync(url, cancellationToken = ct)
            if not resp.IsSuccessStatusCode then
                do! sendMessageAsync bot msg.Chat.Id "❌ Error fetching token data from Dexscreener." ct
            else
                let! body = resp.Content.ReadAsStringAsync()
                let doc = JsonDocument.Parse(body)
                let pairs = doc.RootElement.GetProperty("pairs").EnumerateArray() |> Seq.toList
                let matching = 
                    pairs 
                    |> List.filter (fun p -> 
                        let ca1 = p.GetProperty("baseToken").GetProperty("address").GetString().Trim().ToLowerInvariant()
                        let ca2 = ext.CA.Trim().ToLowerInvariant()
                        let chain1 = p.GetProperty("chainId").GetString().Trim().ToLowerInvariant()
                        let chain2 = ext.ChainId.Trim().ToLowerInvariant()
                        printfn "eredeti address = %s, jsonaddress = %s, eredeti chain = %s, jsonchain = %s" ca2 ca1 chain2 chain1
                        ca1 = ca2 && chain1 = chain2
                    )

                match matching with
                | [] ->
                    do! sendMessageAsync bot msg.Chat.Id "❌ No valid pairs found for this CA." ct
                | [single] ->
                    let baseToken = single.GetProperty("baseToken")
                    let name = baseToken.GetProperty("name").GetString()
                    let chainId = single.GetProperty("chainId").GetString().ToLowerInvariant()
                    let price = 
                        match single.GetProperty("priceUsd").ValueKind with
                        | JsonValueKind.Number -> single.GetProperty("priceUsd").GetRawText()
                        | JsonValueKind.String -> single.GetProperty("priceUsd").GetString()
                        | _ -> "N/A"
                    let pair = single.GetProperty("pairAddress").GetString()
                    let mutable urlValue = Unchecked.defaultof<JsonElement>
                    let link = if single.TryGetProperty("url", &urlValue) then urlValue.GetString() else ""
                    let call = { Name = name; CA = ext.CA; ChainId = chainId; StartPrice = price; Pair = pair; Link = link }
                    alphaCalls.Add(call)
                    saveState()
                    do! sendMessageAsync bot msg.Chat.Id (sprintf "✅ Saved: %s (%s) @ %s USD on %s\nPair: %s\nLink: %s" name ext.CA price chainId pair link) ct
                | multiple when multiple.Length > 1 ->
                    let sb = StringBuilder()
                    sb.AppendLine("Több találat azonos baseToken.address-re és chainId-re:") |> ignore
                    for i = 0 to multiple.Length - 1 do
                        let pool = multiple.[i]
                        let baseToken = pool.GetProperty("baseToken")
                        let quoteToken = pool.GetProperty("quoteToken")
                        let name = baseToken.GetProperty("name").GetString()
                        let symbol = baseToken.GetProperty("symbol").GetString()
                        let chainId = pool.GetProperty("chainId").GetString()
                        let mutable dexIdValue = Unchecked.defaultof<JsonElement>
                        let dexId = if pool.TryGetProperty("dexId", &dexIdValue) then dexIdValue.GetString() else "N/A"
                        let price = 
                            match pool.GetProperty("priceUsd").ValueKind with
                            | JsonValueKind.Number -> pool.GetProperty("priceUsd").GetRawText()
                            | JsonValueKind.String -> pool.GetProperty("priceUsd").GetString()
                            | _ -> "N/A"
                        let mutable urlValue = Unchecked.defaultof<JsonElement>
                        let url = if pool.TryGetProperty("url", &urlValue) then urlValue.GetString() else ""
                        let mutable liquidityValue = Unchecked.defaultof<JsonElement>
                        let liquidity =
                            if pool.TryGetProperty("liquidity", &liquidityValue) then
                                let l = liquidityValue
                                let mutable usdValue = Unchecked.defaultof<JsonElement>
                                if l.TryGetProperty("usd", &usdValue) then usdValue.ToString() else "N/A"
                            else "N/A"
                        let mutable volumeValue = Unchecked.defaultof<JsonElement>
                        let volume =
                            if pool.TryGetProperty("volume", &volumeValue) then
                                let v = volumeValue
                                let mutable h24Value = Unchecked.defaultof<JsonElement>
                                if v.TryGetProperty("h24", &h24Value) then h24Value.ToString() else "N/A"
                            else "N/A"
                        let quoteSymbol = quoteToken.GetProperty("symbol").GetString()
                        sb.AppendLine(sprintf "#%d\nName: %s (%s)\nChain: %s\nDEX: %s\nPair: %s/%s\nPrice: %s USD\nLiquidity: %s USD\n24h Volume: %s\nURL: %s\n----------------------" (i+1) name symbol chainId dexId symbol quoteSymbol price liquidity volume url) |> ignore
                    pendingChoices.[msg.Chat.Id] <- (int msg.From.Id, multiple)
                    let! sentMsg = bot.SendMessage(ChatId.op_Implicit(msg.Chat.Id), sb.ToString() + "\nVálaszolj a sorszámmal a kiválasztáshoz!", cancellationToken = ct)
                    pendingChoiceMessages.[msg.Chat.Id] <- sentMsg.MessageId
                | _ ->
                    do! sendMessageAsync bot msg.Chat.Id "⚠️ Ismeretlen hiba a találatok feldolgozásakor." ct
}

let handleGetAlphaByChatId (bot: ITelegramBotClient) (chatId: int64) (ct: CancellationToken) = task {
    if alphaCalls.Count = 0 then
        do! sendMessageAsync bot chatId "No alpha calls." ct
    else
        let sb = StringBuilder()
        for i in 0 .. alphaCalls.Count - 1 do
            let c = alphaCalls.[i]
            let pairId = if String.IsNullOrWhiteSpace c.Pair then None else Some c.Pair
            let! infoRes = getDexscreenerTokenInfo c.CA c.ChainId pairId
            let pairText = if String.IsNullOrWhiteSpace c.Pair then "-" else c.Pair
            let linkText = if String.IsNullOrWhiteSpace c.Link then "-" else c.Link
            match infoRes with
            | Ok info ->
                let now = Double.Parse(string info.PriceUsd, NumberStyles.Float, CultureInfo.InvariantCulture)
                let start = Double.Parse(string c.StartPrice, NumberStyles.Float, CultureInfo.InvariantCulture)
                let change = if start > 0. then (now - start) / start * 100. else 0.
                sb.AppendLine(
                    sprintf "🔸 Alpha %d:\n • Name: %s\n • CA: %s\n • Chain: %s\n • Start: %s USD\n • Now: %s USD\n • Δ: %.2f%%%%\n • Pair: %s\n • Link: %s\n"
                        (i+1) c.Name c.CA c.ChainId c.StartPrice info.PriceUsd change pairText linkText
                ) |> ignore
            | Error e ->
                printfn "[ALPHA ERROR] %s" e
                if e.Contains("No 'pair' object in response.") then
                    sb.AppendLine(
                        sprintf "🔸 Alpha %d:\n • Name: %s\n • CA: %s\n • Pair: %s\nToken pair is no longer available."
                            (i+1) c.Name c.CA pairText
                    ) |> ignore
                else
                    sb.AppendLine(
                        sprintf "🔸 Alpha %d:\n • Name: %s\n • CA: %s\n • Chain: %s\n • Start: %s USD\n • Now: -\n • Δ: -\n • Pair: %s\n • Link: %s\n[Hiba: %s]"
                            (i+1) c.Name c.CA c.ChainId c.StartPrice pairText linkText e
                    ) |> ignore

        let button = InlineKeyboardButton.WithUrl("Open social tracker", "https://wepefansocialtracker.duckdns.org/")
        let markup = InlineKeyboardMarkup(seq { yield seq { yield button } })
        let! _ = bot.SendMessage(
            ChatId.op_Implicit(chatId),
            sb.ToString(),
            replyMarkup = markup,
            cancellationToken = ct
        )
        return ()
}

let handleRemoveAlpha (bot: ITelegramBotClient) (msg: Message) (args: string) (ct: CancellationToken) = task {
    let parseResult = System.Int32.TryParse(args)
    if parseResult |> fst && (let idx = snd parseResult in idx > 0 && idx <= alphaCalls.Count) then
        let idx = snd parseResult
        let removed = alphaCalls.[idx - 1]
        alphaCalls.RemoveAt(idx - 1)
        saveState()
        do! sendMessageAsync bot msg.Chat.Id (sprintf "✅ Removed alpha call #%d: %s" idx removed.Name) ct
    else
        do! sendMessageAsync bot msg.Chat.Id "Please provide a valid index. Usage: /removecall <index>" ct
    return ()
}

let handleChangeStart (bot: ITelegramBotClient) (msg: Message) (args: string) (ct: CancellationToken) = task {
    let parts = args.Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
    if parts.Length <> 2 then
        do! sendMessageAsync bot msg.Chat.Id "Usage: /changestart <index> <newprice>" ct
    else
        let idxParsed, idx = System.Int32.TryParse(parts.[0])
        let priceParsed, newPrice = Double.TryParse(parts.[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
        if not idxParsed || not priceParsed || idx <= 0 || idx > alphaCalls.Count || newPrice < 0.0 then
            do! sendMessageAsync bot msg.Chat.Id "Invalid index or price. Usage: /changestart <index> <newprice>" ct
        else
            let call = alphaCalls.[idx - 1]
            alphaCalls.[idx - 1] <- { call with StartPrice = newPrice.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            saveState()
            do! sendMessageAsync bot msg.Chat.Id (sprintf "✅ Start price for #%d (%s) changed to %s USD" idx call.Name (newPrice.ToString(System.Globalization.CultureInfo.InvariantCulture))) ct
    return ()
}

let mutable loopActive = false
let mutable loopInterval = 10
let mutable loopCts = new CancellationTokenSource()

let rec alphaLoop (bot: ITelegramBotClient) (chatId: int64) = task {
    while loopActive do
        try
            do! handleGetAlphaByChatId bot chatId cts.Token
        with ex ->
            printfn "[LOOP ERROR] %s" ex.Message
        do! Task.Delay(TimeSpan.FromMinutes(float loopInterval), loopCts.Token)
}

let handleStart (bot: ITelegramBotClient) (msg: Message) (ct: CancellationToken) = task {
    if not loopActive then
        loopActive <- true
        loopCts.Dispose()
        let newCts = new CancellationTokenSource()
        loopCts <- newCts
        do! sendMessageAsync bot msg.Chat.Id "Alpha loop started." ct
        alphaLoop bot msg.Chat.Id |> ignore
    else
        do! sendMessageAsync bot msg.Chat.Id "Loop already running." ct
}

let handleStop (bot: ITelegramBotClient) (msg: Message) (ct: CancellationToken) = task {
    if loopActive then
        loopActive <- false
        loopCts.Cancel()
        do! sendMessageAsync bot msg.Chat.Id "Alpha loop stopped." ct
    else
        do! sendMessageAsync bot msg.Chat.Id "Loop is not running." ct
}

let handleInterval (bot: ITelegramBotClient) (msg: Message) (args: string) (ct: CancellationToken) = task {
    match System.Int32.TryParse(args) with
    | true, v when v > 0 ->
        loopInterval <- v
        do! sendMessageAsync bot msg.Chat.Id (sprintf "Interval set to %d minutes." v) ct
    | _ ->
        do! sendMessageAsync bot msg.Chat.Id "Usage: /interval <minutes> (must be positive integer)" ct
}

let handleUpdate (bot: ITelegramBotClient) (update: Update) (ct: CancellationToken) = task {
    match update.Message with
    | null -> ()
    | msg when isNull msg.Text -> ()
    | msg ->

        if pendingChoices.ContainsKey(msg.Chat.Id) then
            let userId, options = pendingChoices.[msg.Chat.Id]
            if int msg.From.Id <> userId then
                do! sendMessageAsync bot msg.Chat.Id "❌ Only the original requester can select a CA." ct
                return ()
            let text = msg.Text.Trim()
            match System.Int32.TryParse(text) with
            | true, index ->
                if index > 0 && index <= List.length options then
                    match pendingChoiceMessages.TryGetValue(msg.Chat.Id) with
                    | true, messageId ->
                        try
                            bot.DeleteMessage(msg.Chat.Id, messageId) |> ignore
                        with _ -> ()
                        pendingChoiceMessages.Remove(msg.Chat.Id) |> ignore
                    | _ -> ()
                    let selected = options.[index - 1]
                    let baseToken = selected.GetProperty("baseToken")
                    let name = baseToken.GetProperty("name").GetString()
                    let ca = baseToken.GetProperty("address").GetString()
                    let chainId = selected.GetProperty("chainId").GetString().ToLowerInvariant()
                    let price =
                        match selected.GetProperty("priceUsd").ValueKind with
                        | JsonValueKind.Number -> selected.GetProperty("priceUsd").GetRawText()
                        | JsonValueKind.String -> selected.GetProperty("priceUsd").GetString()
                        | _ -> "N/A"
                    let pair = selected.GetProperty("pairAddress").GetString()
                    let mutable urlValue = Unchecked.defaultof<JsonElement>
                    let link = if selected.TryGetProperty("url", &urlValue) then urlValue.GetString() else ""
                    let call = { Name = name; CA = ca; ChainId = chainId; StartPrice = price; Pair = pair; Link = link }
                    alphaCalls.Add(call)
                    saveState()
                    pendingChoices.Remove(msg.Chat.Id) |> ignore
                    do! sendMessageAsync bot msg.Chat.Id (sprintf "✅ Saved: %s (%s) @ %s USD on %s\nPair: %s\nLink: %s" name ca price chainId pair link) ct
                else
                    do! sendMessageAsync bot msg.Chat.Id "Invalid index. Please try again." ct
                return ()
            | _ ->
                do! sendMessageAsync bot msg.Chat.Id "Please reply with a number." ct
                return ()

        if isNull msg.From then
            do! sendMessageAsync bot msg.Chat.Id "❌ Only admins." ct
        else
            let! chatMember = bot.GetChatMember(msg.Chat.Id, msg.From.Id)
            let isAdmin = chatMember.Status = ChatMemberStatus.Administrator || chatMember.Status = ChatMemberStatus.Creator
            if not isAdmin then
                do! sendMessageAsync bot msg.Chat.Id "❌ Only admins." ct
            else
                if msg.Text.StartsWith("/addalphacall") then
                    let args = msg.Text.Substring("/addalphacall".Length).Trim()
                    do! handleAddAlpha bot msg args ct
                elif msg.Text.StartsWith("/getalpha") then
                    do! handleGetAlphaByChatId bot msg.Chat.Id ct
                elif msg.Text.StartsWith("/removecall") then
                    let args = msg.Text.Substring("/removecall".Length).Trim()
                    do! handleRemoveAlpha bot msg args ct
                elif msg.Text.StartsWith("/start") then
                    do! handleStart bot msg ct
                elif msg.Text.StartsWith("/stop") then
                    do! handleStop bot msg ct
                elif msg.Text.StartsWith("/interval") then
                    let args = msg.Text.Substring("/interval".Length).Trim()
                    do! handleInterval bot msg args ct
                elif msg.Text.StartsWith("/changestart") then
                    let args = msg.Text.Substring("/changestart".Length).Trim()
                    do! handleChangeStart bot msg args ct
                else ()
}

let handleError (bot: ITelegramBotClient) (ex: Exception) (ct: CancellationToken) = task {
    printfn "Error: %O" ex
}

[<EntryPoint>]
let main _ =
    loadState()
    printLoadedAlphaCalls()
    let updateHandler (bot: ITelegramBotClient) (update: Update) (ct: CancellationToken) =
        handleUpdate bot update ct |> Async.AwaitTask |> Async.RunSynchronously
    let errorHandler (bot: ITelegramBotClient) (ex: exn) (ct: CancellationToken) =
        handleError bot ex ct |> Async.AwaitTask |> Async.RunSynchronously
    botClient.StartReceiving(updateHandler, errorHandler, receiverOptions, cts.Token)
    printfn "Bot started. Use /addalphacall and /getalpha."
    Task.Delay(-1, cts.Token).GetAwaiter().GetResult()
    0
