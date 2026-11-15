# Alidade connection contract

Alidade requests use an ephemeral `http://127.0.0.1:<port>` listener. The HTTP client uses the advertised numeric address and disables redirects, cookies and proxies. The listener validates the local peer and exact Host. Browser requests with an Origin are rejected on the native HTTP routes. Responses use `Cache-Control: no-store`.

This contract has two version numbers: the connection protocol / HTTP API version and the exchange collection's `schemaVersion`. The [connection examples](fixtures/connection.json) contain only synthetic IDs and credentials.

## Studio bridge

Studio obtains an endpoint, a WebSocket URL and a one-use ticket from its host. Web tickets are bound to the requesting origin. A Desktop ticket can be issued for the trusted WebView's actual origin, including `null` when the WebView uses an opaque origin. A ticket expires after 30 seconds. The first message must be an `attach` control message within 3 seconds. The server acknowledges an accepted ticket with `attached`.

The attached socket registers each running Project by its runtime UUID and waits for `registered`. The `unregister` control message removes that runtime's route. Closing the socket removes all its routes and terminates its outstanding relays. Each newly registered Project lifetime has a new UUID.

Control messages use text frames. Each binary request or response frame contains a four-byte unsigned big-endian JSON header length, the UTF-8 header, and the original HTTP body bytes. The header limit is 16 KiB. A body-less message ends after the header. Route values in this revision are `pair` and `project`. A request's `bearer` field, when present, contains the complete credential following `Bearer `. The server accepts a response only from the socket which received the request. It preserves only supported response headers.

## Native HTTP API

| Method and path | Input | Response |
| --- | --- | --- |
| `POST /v1/pair` | A UTF-8 JSON body of at most 4 KiB with `invite`, `clientName`, `apiVersion` and `collectionVersions`. | The Project identity and capabilities, `grantId`, `accessToken`, and UTC `expiresAt`. |
| `GET /v1/project` | `Authorization: Bearer <accessToken>`. | The current Project identity, readiness and capabilities for that grant. |

An invite and an access token both have the form `<instance UUID>.<base64url secret>`. The secret is 32 bytes with no base64 padding. The server uses the UUID to route the request. Studio validates the full credential against the running Project. A copied invitation is `ALIDADE1.` followed by unpadded base64url UTF-8 JSON containing `endpoint` and `invite`.

A consumed, expired or unknown invite returns `401 INVITE_INVALID`. An expired, revoked or unknown grant returns `401 PAIRING_REQUIRED`. Malformed requests use `INVALID_REQUEST`. A non-JSON pair request uses `415 UNSUPPORTED_MEDIA_TYPE` and an oversized body uses `413 CONTENT_TOO_LARGE`. A dropped or timed-out relay uses `503 BRIDGE_UNAVAILABLE`. At capacity use `429 CAPACITY_EXCEEDED`. Error bodies have an `error` object with `code` and `message`.

Pairing, revocation and Project readiness are Studio responsibilities. Runtime availability and limits are reported by the running host. Later geometry operations use the same bridge and credentials.
