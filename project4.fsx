#time "on"
#r "nuget: Akka.FSharp, 1.4.12"
#r "nuget: Akka.Remote, 1.4.12"
#r "nuget: Newtonsoft.Json, 12.0.3"
#r "nuget: Suave, 2.6.0"
open System
open System.Collections.Generic
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.ServerErrors
open Suave.Writers
open Newtonsoft.Json
open Akka.Actor
open Akka.Configuration
open Akka.FSharp

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket


let setCORSHeaders =
    setHeader  "Access-Control-Allow-Origin" "*"
    >=> setHeader "Access-Control-Allow-Headers" "content-type"

let system = ActorSystem.Create("TwitterEngine")

//types
type OtherAns = {
        Text: string
    }
type Ans = {
        Text: string
        AnswerId: int
    }
type ResponseMessage = {
        Comment: string
        Content: list<string>
        status: int
        error: bool
    }
      
type TwitterRegister = {
        UserName: string
        Password: string
    }
type TwitterLogin = {
        UserName: string
        Password: string
    }
type TwitterLogout = {
        UserName: string
    }

type Followers = {
        UserName: string
        Following: string
    }
type NewTweet = {
        Tweet: string
        UserName: string
    }

let buildByteResponseToWS (message:string) =
    message
    |> System.Text.Encoding.ASCII.GetBytes
    |> ByteSegment

let mutable users = Map.empty
let mutable usersTracker = Map.empty
let mutable tweetOwner = Map.empty
let mutable followers = Map.empty
let mutable mentions = Map.empty
let mutable hashTags = Map.empty
let mutable websockmap = Map.empty


module userFunctions = 

    let doesTheUserExist username =
        let current = users.TryFind(username)
        current <> None

    let addUser (user: TwitterRegister) =
        let currUser = user.UserName
        let currPassword = user.Password
        let current = users.TryFind(user.UserName)

        if current = None then
            users <- users.Add(user.UserName,user.Password)
            {Comment = "User registration successful for " +  currUser + " ! Hurray!" ; Content = [] ; status = 1; error = false}
        else
            {Comment = currUser + " is and existing user! Please use another id or login" ; Content = []; status = 1; error = true}
    

    module logInStatus = 
        let userLogin (user: TwitterLogin) = 
            let currUser = user.UserName
            let currPassword = user.Password
            printfn "Login user - %s, details - %A" currUser user
            
            let current = users.TryFind(user.UserName)
            if (current.IsNone = false) then
                printfn "User actual pwd: %s" current.Value
            else
                printfn "Current is None"
            // printfn "real details of %s - %A" currUser current
            // printfn "%A" current.Value
            // let realPass = current.Value
            // printfn "%A" realPass

            if current = None then
                {Comment = currUser + " Doesn't exsist! Please Register or check the id" ; Content = []; status = 0; error = true}
            else
                let userPassword = current.Value;
                if(userPassword.Equals(currPassword) = false) then 
                    {Comment = "Incorrect Password"; Content=[]; status=0; error=true}
                else 
                    let current1 = usersTracker.TryFind(user.UserName)
                    if current1 = None then
                        usersTracker <- usersTracker.Add(user.UserName,true)
                        {Comment = "Login successful! Welcome " + currUser ; Content = []; status = 2; error = false}
                    else
                        {Comment = "Hello " + currUser + "! You are already logged in!" ; Content=[]; status=0; error=true}
                    

        let userLogOut (user:TwitterLogout) = 
            let currUser = user.UserName
            printfn "Logout user - %s, details - %A" currUser user
            let current = users.TryFind(user.UserName)

            if current = None then
                {Comment = currUser + " Doesn't exsist! Please Register or check the id" ; Content=[]; status=0; error=true}
            else
                let current1 = usersTracker.TryFind(user.UserName)
                if current1 = None then
                    {Comment = "UPlease login";Content=[];status=1;error=true}
                else
                    usersTracker <- usersTracker.Remove(user.UserName)
                    {Comment = "You have been logged out successfully!"; Content=[]; status=1; error=false}

        let loggedInUser username = 
            let current = usersTracker.TryFind(username)
            if current <> None then
                1
            else
                let current1 = users.TryFind(username)
                if current1 = None then -1 else 0


module twitterHandler = 
    type TwitterHandlerMessage =
        | Tweet of WebSocket * NewTweet
        | TweetSend of WebSocket*NewTweet
        | Mention of WebSocket* NewTweet
        | Following of WebSocket * string

    let twitterHandler (mailbox:Actor<_>) = 
        let rec loop() = actor{
            let! message = mailbox.Receive()
            match message with
            |Tweet(ws,tweet)->  let response = "You have tweeted '"+tweet.Tweet+"'"
                                let byteResponse = buildByteResponseToWS response
                                let s = socket{
                                                do! ws.send Text byteResponse true
                                                }
                                Async.StartAsTask s |> ignore
            |TweetSend(ws,tweet)->
                                    let response = tweet.UserName+" has tweeted '"+tweet.Tweet+"'"
                                    let byteResponse = buildByteResponseToWS response
                                    let s =socket{
                                                    do! ws.send Text byteResponse true
                                                    }
                                    Async.StartAsTask s |> ignore
            |Mention(ws,tweet)->
                                    let response = tweet.UserName+" mentioned you in tweet '"+tweet.Tweet+"'"
                                    let byteResponse = buildByteResponseToWS response
                                    let s =socket{
                                                    do! ws.send Text byteResponse true
                                                    }
                                    Async.StartAsTask s |> ignore
            |Following(ws,msg)->
                                    let response = msg
                                    let byteResponse = buildByteResponseToWS response
                                    let s =socket{
                                                    do! ws.send Text byteResponse true
                                                    }
                                    Async.StartAsTask s |> ignore
            return! loop()
        }
        loop()
    let twitterHandlerRef = spawn system "tref" twitterHandler


module parseTweets  = 
    let tweetParser (tweet:NewTweet) =
        let splits = (tweet.Tweet.Split ' ')
        // let mutable op = ""
        for i in splits do
            if i.StartsWith "@" then
                let current = i.Split '@'
                if userFunctions.doesTheUserExist current.[1] then
                    let current1 = mentions.TryFind(current.[1])
                    if current1 = None then
                        let mutable mapData = Map.empty
                        let tlist = new List<string>()
                        tlist.Add(tweet.Tweet)
                        mapData <- mapData.Add(tweet.UserName,tlist)
                        mentions <- mentions.Add(current.[1],mapData)
                    else
                        let current2 = current1.Value.TryFind(tweet.UserName)
                        if current2 = None then
                            let tlist = new List<string>()
                            tlist.Add(tweet.Tweet)
                            let mutable mapData = current1.Value
                            mapData <- mapData.Add(tweet.UserName,tlist)
                            mentions <- mentions.Add(current.[1],mapData)
                        else
                            current2.Value.Add(tweet.Tweet)
                    let current3 = websockmap.TryFind(current.[1])
                    if current3<>None then
                        twitterHandler.twitterHandlerRef <! twitterHandler.Mention(current3.Value,tweet)

            elif i.StartsWith "#" then
                let current1 = i.Split '#'
                let current = hashTags.TryFind(current1.[1])
                if current = None then
                    let list = List<string>()
                    list.Add(tweet.Tweet)
                    hashTags <- hashTags.Add(current1.[1],list)
                else
                    current.Value.Add(tweet.Tweet)


module twitterFunctions = 

    let followersAdd (follower: Followers) =
        let currFollower = follower.Following
        printfn "Received Follower Request from %s as %A" currFollower follower
        let status = userFunctions.logInStatus.loggedInUser follower.UserName

        if status = 1 then
            if (userFunctions.doesTheUserExist follower.Following) then
                let current = followers.TryFind(follower.Following)
                let current1 = websockmap.TryFind(follower.UserName)
                if current = None then
                    let list = new List<string>()
                    list.Add(follower.UserName)
                    followers <- followers.Add(follower.Following,list)
                    if current1 <> None then
                        twitterHandler.twitterHandlerRef <! twitterHandler.Following(current1.Value,"You are now following: "+follower.Following)
                    {Comment = "Sucessfully Added to the Following list";Content=[];status=2;error=false}
                else
                    if current.Value.Exists( fun x -> x.CompareTo(follower.UserName) = 0 ) then
                        if current1 <> None then
                            twitterHandler.twitterHandlerRef <! twitterHandler.Following(current1.Value,"You are already following: "+follower.Following)
                        {Comment = "You are already Following"+follower.Following;Content=[];status=2;error=true}
                    else
                        current.Value.Add(follower.UserName)
                        if current1 <> None then
                            twitterHandler.twitterHandlerRef <! twitterHandler.Following(current1.Value,"You are now following: " + follower.Following)
                        {Comment = "Sucessfully Added to the Following list";Content=[];status=2;error=false}
            else
                {Comment = "Follower "+ follower.Following + " doesn't exsist"; Content=[]; status=2; error=true}
        elif status = 0 then
            {Comment = "Login please!"; Content=[]; status=1; error=true}
        else
            {Comment = "User Doesn't Exsist! Please Register"; Content=[]; status=0; error=true}


    let tweetIt (tweet: NewTweet) =
        let current = tweetOwner.TryFind(tweet.UserName)
        if current = None then
            let list = new List<string>()
            list.Add(tweet.Tweet)
            tweetOwner <- tweetOwner.Add(tweet.UserName,list)
        else
            current.Value.Add(tweet.Tweet)
        

    let tweetItToFollowers (tweet: NewTweet) = 
        let current = followers.TryFind(tweet.UserName)
        if current <> None then
            for i in current.Value do
                let current1 = {Tweet=tweet.Tweet;UserName=i}
                tweetIt current1
                let current2 = websockmap.TryFind(i)
                printfn "%s" i
                if current2 <> None then
                    twitterHandler.twitterHandlerRef <! twitterHandler.TweetSend(current2.Value,tweet)


module tweetHandler = 
    type tweetHandlerMsg =
        | AddTweetMessage of NewTweet
        | AddTweetToFollowersMessage of NewTweet
        | TweetParserMessage of NewTweet

    let tweetHandler (mailbox:Actor<_>) =
        let rec loop() = actor{
            let! msg = mailbox.Receive()
            match msg with 
            | AddTweetMessage(tweet) -> twitterFunctions.tweetIt(tweet)
                                        let current = websockmap.TryFind(tweet.UserName)
                                        if current <> None then
                                            twitterHandler.twitterHandlerRef <! twitterHandler.Tweet(current.Value,tweet)
            | AddTweetToFollowersMessage(tweet) ->  twitterFunctions.tweetItToFollowers(tweet)
            | TweetParserMessage(tweet) -> parseTweets.tweetParser(tweet)
            return! loop()
        }
        loop()

    let tweetHandlerRef = spawn system "thref" tweetHandler


module twitterActivities = 

    let registerNewUser (user: TwitterRegister) =
        printfn "Received Register Request from %s as %A" user.UserName user
        userFunctions.addUser user

    let tweetItToUser (tweet: NewTweet) =
        let status = userFunctions.logInStatus.loggedInUser tweet.UserName
        if status = 1 then
            tweetHandler.tweetHandlerRef <! tweetHandler.AddTweetMessage(tweet)
            tweetHandler.tweetHandlerRef <! tweetHandler.AddTweetToFollowersMessage(tweet)
            tweetHandler.tweetHandlerRef <! tweetHandler.TweetParserMessage(tweet)
            {Comment = "Tweeted Succesfully";Content=[];status=2;error=false}
        elif status = 0 then
            {Comment = "Please Login"; Content=[]; status=1; error=true}
        else
            {Comment = "User Doesn't Exsist!!Please Register"; Content=[]; status=0; error=true}

    let reponseTweet (tweet: NewTweet) =
        printfn "Received Tweet Request from %s as %A" tweet.UserName tweet
        tweetItToUser tweet

    module twitterStuff = 
        let getTweets username =
            let status = userFunctions.logInStatus.loggedInUser username
            if status = 1 then
                let current = tweetOwner.TryFind(username)
                if current = None then
                    {Comment = "No Tweets available!";Content=[];status=2;error=false}
                else
                    let len = Math.Min(10,current.Value.Count)
                    let res = [for i in 1 .. len do yield(current.Value.[i-1])] 
                    {Comment = "got your tweets!";Content=res;status=2;error=false}
            elif status = 0 then
                {Comment = "Please Login!";Content=[];status=1;error=true}
            else
                {Comment = "User Doesn't Exsist! Please Register!";Content=[];status=0;error=true}

        let getHashTags username hashtag =
            let status = userFunctions.logInStatus.loggedInUser username
            if status = 1 then
                printf "%s" hashtag
                let current = hashTags.TryFind(hashtag)
                if current = None then
                    {Comment = "No Tweets with this hashtag found"; Content=[]; status=2; error=false}
                else
                    let len = Math.Min(10, current.Value.Count)
                    let res = [for i in 1 .. len do yield(current.Value.[i-1])] 
                    {Comment = "got your hashtags!"; Content=res; status=2; error=false}
            elif status = 0 then
                {Comment = "Please Login";Content=[];status=1;error=true}
            else
                {Comment = "User Doesn't Exsist! Please Register";Content=[];status=0;error=true}

        let getMentions username = 
            let status = userFunctions.logInStatus.loggedInUser username
            if status = 1 then
                let current = mentions.TryFind(username)
                if current = None then
                    {Comment = "No Mentions found!";Content=[];status=2;error=false}
                else
                    let res = new List<string>()
                    for i in current.Value do
                        for j in i.Value do
                            res.Add(j)
                    let len = Math.Min(10,res.Count)
                    let res1 = [for i in 1 .. len do yield(res.[i-1])] 
                    {Comment = "got your mentions!";Content=res1;status=2;error=false}
            elif status = 0 then
                {Comment = "Please Login";Content=[];status=1;error=true}
            else
                {Comment = "User Doesn't Exsist! Please Register";Content=[];status=0;error=true}


module connectors = 
    let getString (rawForm: byte[]) =
        // printfn "%A" (System.Text.Encoding.UTF8.GetString(rawForm))
        System.Text.Encoding.UTF8.GetString(rawForm)

    let fromJson<'a> json =
        JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a


module routingFunctions = 
    module toGetIn = 
        let register =
            request (fun r ->
            r.rawForm
            |> connectors.getString
            |> connectors.fromJson<TwitterRegister>
            |> twitterActivities.registerNewUser
            |> JsonConvert.SerializeObject
            |> OK
            )
            >=> setMimeType "application/json"
            >=> setCORSHeaders

        let login =
            request (fun r ->
            r.rawForm
            |> connectors.getString
            |> connectors.fromJson<TwitterLogin>
            |> userFunctions.logInStatus.userLogin
            |> JsonConvert.SerializeObject
            |> OK
            )
            >=> setMimeType "application/json"
            >=> setCORSHeaders

        let logout =
            request (fun r ->
            r.rawForm
            |> connectors.getString
            |> connectors.fromJson<TwitterLogout>
            |> userFunctions.logInStatus.userLogOut
            |> JsonConvert.SerializeObject
            |> OK
            )
            >=> setMimeType "application/json"
            >=> setCORSHeaders

    module twitterGets = 
        let gettweets username =
            printfn "Received GetTweets Request from %s " username
            twitterActivities.twitterStuff.getTweets username
            |> JsonConvert.SerializeObject
            |> OK
            >=> setMimeType "application/json"
            >=> setCORSHeaders

        let getmentions username =
            printfn "Received GetMentions Request from %s " username
            twitterActivities.twitterStuff.getMentions username
            |> JsonConvert.SerializeObject
            |> OK
            >=> setMimeType "application/json"
            >=> setCORSHeaders

        let gethashtags username hashtag =
            printfn "Received GetHashTag Request from %s for hashtag %A" username hashtag
            twitterActivities.twitterStuff.getHashTags username hashtag
            |> JsonConvert.SerializeObject
            |> OK
            >=> setMimeType "application/json"
            >=> setCORSHeaders

    
    module twitterSpecific = 
        let newTweet = 
            request (fun r ->
            r.rawForm
            |> connectors.getString
            |> connectors.fromJson<NewTweet>
            |> twitterActivities.reponseTweet
            |> JsonConvert.SerializeObject
            |> OK
            )
            >=> setMimeType "application/json"
            >=> setCORSHeaders

        let follow =
            request (fun r ->
            r.rawForm
            |> connectors.getString
            |> connectors.fromJson<Followers>
            |> twitterFunctions.followersAdd
            |> JsonConvert.SerializeObject
            |> OK
            )
            >=> setMimeType "application/json"
            >=> setCORSHeaders


module wsHandlerFunction = 
    let websocketHandler (webSocket : WebSocket) (context: HttpContext) =
        socket {
            let mutable loop = true

            while loop do
                let! msg = webSocket.read()

                match msg with
                | (Text, data, true) ->
                    // the message can be converted to a string
                    // printfn "%A" Text
                    let str = UTF8.toString data 
                    // printfn "%s" str
                    if str.StartsWith("UserName:") then
                        let uname = str.Split(':').[1]
                        websockmap <- websockmap.Add(uname,webSocket)
                        printfn "connected to %s websocket" uname
                    else
                        let response = sprintf "response to %s" str
                        let byteResponse = buildByteResponseToWS response
                        do! webSocket.send Text byteResponse true

                | (Close, _, _) ->
                    let emptyResponse = [||] |> ByteSegment
                    do! webSocket.send Close emptyResponse true
                    loop <- false
                | _ -> ()
        }

let allow_cors : WebPart =
    choose [
        OPTIONS >=>
            fun context ->
                context |> (
                    setCORSHeaders
                    >=> OK "CORS approved" )
    ]

module mainConnect = 
    let app =
        choose
            [ 
                path "/websocket" >=> handShake wsHandlerFunction.websocketHandler 
                allow_cors
                GET >=> choose
                    [ 
                    path "/" >=> OK "Hello World" 
                    pathScan "/gettweets/%s" (fun username -> (routingFunctions.twitterGets.gettweets username))
                    pathScan "/getmentions/%s" (fun username -> (routingFunctions.twitterGets.getmentions username))
                    pathScan "/gethashtags/%s/%s" (fun (username,hashtag) -> (routingFunctions.twitterGets.gethashtags username hashtag))
                    ]

                POST >=> choose
                    [   
                    path "/newtweet" >=> routingFunctions.twitterSpecific.newTweet 
                    path "/register" >=> routingFunctions.toGetIn.register
                    path "/login" >=> routingFunctions.toGetIn.login
                    path "/logout" >=> routingFunctions.toGetIn.logout
                    path "/follow" >=> routingFunctions.twitterSpecific.follow
                ]

                PUT >=> choose
                    [ ]

                DELETE >=> choose
                    [ ]
            ]

startWebServer defaultConfig mainConnect.app
