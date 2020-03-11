module Gir.Cart.CheckoutIntegration

open FSharp.Data
open Microsoft.IdentityModel.JsonWebTokens
open Microsoft.IdentityModel.Tokens
open Thoth.Json.Net
open Gir.Domain
open Gir.Encoders
open Gir.Decoders


let mutable partnerAccessTokenCache: string option = None

let mutable purchaseIdCache: string option = None

let decodePartnerAccessToken =
    partnerAccessTokenDecoder
    >> function
    | Ok v -> v
    | Error e -> failwithf "Cannot decode partner access token, error = %A" e

let getPartnerAccessToken url clientId clientSecret =
    let getPartnerAccessTokenPayload = getPartnerTokenPayloadEncoder clientId clientSecret
    Http.RequestString
        (url, headers = [ ("Content-Type", "application/json") ], body = TextRequest getPartnerAccessTokenPayload,
         httpMethod = "POST") |> decodePartnerAccessToken

let createValidationParameters =
    let validationParameters = TokenValidationParameters()
    validationParameters.ValidateLifetime <- true
    validationParameters

let isValid t =
    let handler = JsonWebTokenHandler()
    let validationResult = handler.ValidateToken(t, createValidationParameters)
    if validationResult.IsValid then (Some t) else None

let getCachedToken url clientId clientSecret =
    partnerAccessTokenCache
    |> Option.bind isValid
    |> Option.defaultWith (fun _ ->
        let token = getPartnerAccessToken url clientId clientSecret
        partnerAccessTokenCache <- Some token
        token)

let decodePurchaseToken =
    purchaseTokenDecoder
    >> function
    | Ok v -> v
    | Error e -> failwithf "Cannot decode purchase token, error = %A" e

let initPaymentPayloadDecoder =
    Decode.object (fun get ->
        { PurchaseId = get.Required.Field "purchaseId" Decode.string
          Jwt = get.Required.Field "jwt" Decode.string })

let initPaymentDecoder s =
    match Decode.fromString initPaymentPayloadDecoder s with
    | Ok v ->
        purchaseIdCache <- Some v.PurchaseId
        v.Jwt
    | Error e -> failwithf "Cannot decode init payment, error = %A" e

let reclaimPurchaseToken backendUrl partnerToken =
    let purchaseId =
        match purchaseIdCache with
        | Some purchaseId -> purchaseId
        | None -> failwith "Cannot reclaim token, purchase not initialized"

    let bearerString = "Bearer " + partnerToken
    let url = sprintf "%s/api/partner/payments/%s/token" backendUrl purchaseId
    Http.RequestString
        (url,
         headers =
             [ ("Content-Type", "application/json")
               ("Authorization", bearerString) ], httpMethod = "GET")
    |> decodePurchaseToken

let getPurchaseToken backendUrl (cartState: CartState) partnerToken =
    if (List.isEmpty cartState.Items) then
        ""
    else
        match purchaseIdCache with
        | Some _ -> reclaimPurchaseToken backendUrl partnerToken
        | None ->
            let encodedPaymentPayload = paymentPayloadEncoder cartState.Items

            let bearerString = "Bearer " + partnerToken
            Http.RequestString
                (sprintf "%s/api/partner/payments" backendUrl,
                 headers =
                     [ ("Content-Type", "application/json")
                       ("Authorization", bearerString) ], body = TextRequest encodedPaymentPayload, httpMethod = "POST")
            |> initPaymentDecoder

let updateItems backendUrl cartState partnerToken =
    if (List.isEmpty cartState.Items) then
        purchaseIdCache <- None
        ""
    else
        match purchaseIdCache with
        | Some purchaseId ->
            let encodedPaymentPayload = paymentPayloadEncoder cartState.Items
            let bearerString = "Bearer " + partnerToken
            let url = sprintf "%s/api/partner/payments/%s/items" backendUrl purchaseId
            Http.RequestString
                (url,
                 headers =
                     [ ("Content-Type", "application/json")
                       ("Authorization", bearerString) ], body = TextRequest encodedPaymentPayload, httpMethod = "PUT")
        | None -> getPurchaseToken backendUrl cartState partnerToken
