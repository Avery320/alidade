# Alidade connection contract

Alidade requests use an ephemeral `http://127.0.0.1:<port>` listener. The HTTP client uses the advertised numeric address and disables redirects, cookies and proxies. The listener validates the local peer and exact Host. Browser requests with an Origin are rejected on the native HTTP routes. Responses use `Cache-Control: no-store`.

This contract has two version numbers: the connection protocol / HTTP API version and the exchange collection's `schemaVersion`. The [connection examples](fixtures/connection.json) contain only synthetic IDs and credentials.

## Studio bridge

Studio obtains an endpoint, a WebSocket URL and a one-use ticket from its host. Web tickets are bound to the requesting origin. A Desktop ticket can be issued for the trusted WebView's actual origin, including `null` when the WebView uses an opaque origin. A ticket expires after 30 seconds. The first message must be an `attach` control message within 3 seconds. The server acknowledges an accepted ticket with `attached`.

The attached socket registers each running Project by its runtime UUID and waits for `registered`. The `unregister` control message removes that runtime's route. Closing the socket removes all its routes and terminates its outstanding relays. Each newly registered Project lifetime has a new UUID.

Control messages use text frames. Each binary request or response frame contains a four-byte unsigned big-endian JSON header length, the UTF-8 header, and the original HTTP body bytes. The header limit is 16 KiB. A body-less message ends after the header. A request's `bearer` field contains the complete credential following `Bearer `. Geometry mutations carry the original `mediaType`, `requestId`, `bodySha256` and, for replacement, `entityId` and `ifMatch`. The server accepts a response only from the socket which received the request. Approved response fields include `etag`, `location` and `retryAfterSeconds`.

## Native HTTP API

| Method and path | Input | Response |
| --- | --- | --- |
| `POST /v1/pair` | A UTF-8 JSON body of at most 4 KiB with `invite`, `clientName`, `apiVersion` and `collectionVersions`. | The Project identity and capabilities, `grantId`, `accessToken`, and UTC `expiresAt`. |
| `GET /v1/project` | `Authorization: Bearer <accessToken>`. | The current Project identity, readiness and capabilities for that grant. |
| `GET /v1/geometry` | Bearer. | `200` with `{items}`. Entries have `entityId`, `name`, `parentPath`, `etag`, `canRead`, `canReplace` and optional `reason`. |
| `GET /v1/geometry/{entityId}` | Bearer. Percent-encode the Entity ID as one path segment. | `200` with the current geometry collection and strong `ETag`. |
| `POST /v1/geometry` | Bearer, `X-Alidade-Request-Id` UUID and JSON `{name,collection,parentFrameEntityId?}`. | `202` with a relative receipt URL in `Location`. |
| `PUT /v1/geometry/{entityId}` | Bearer, request ID, a single strong `If-Match` from a read or receipt, and JSON `{collection}`. | `202` with a relative receipt URL or `412 STALE_REVISION`. Missing `If-Match` gives `428 PRECONDITION_REQUIRED`. |
| `GET /v1/requests/{requestId}` | The same grant as the mutation. | `200` with `{requestId,state,result?}` or `404 REQUEST_UNKNOWN`. |

The corresponding geometry bridge route names are `geometry-list`, `geometry-read`, `geometry-create`, `geometry-replace` and `receipt`. Geometry requests and responses are limited to 8 MiB in v1.

`202` records admission. Follow the same host's relative `/v1/requests/{requestId}` to read the receipt. A receipt is `pending`, `committed` or `rejected`. A committed result contains `httpStatus` (201 create or 200 replace), `entityId` and `etag`. A rejected result contains `httpStatus` and an `error` object. A committed receipt confirms the running Station transaction. Save is a separate Project operation.

For a retry, resend the same UUID, URL, conditions, media type and exact body bytes. The server returns the same receipt URL for an admitted repeat, even if the Project was edited or the original operation was undone. Reusing the UUID for different input gives `409 REQUEST_ID_CONFLICT`. Pre-admission busy and capacity responses leave the ID available for the same request. A terminal rejection requires a new, explicit operation.

An invite and an access token both have the form `<instance UUID>.<base64url secret>`. The secret is 32 bytes with no base64 padding. The server uses the UUID to route the request. Studio validates the full credential against the running Project. A copied invitation is `ALIDADE1.` followed by unpadded base64url UTF-8 JSON containing `endpoint` and `invite`.

A consumed, expired or unknown invite returns `401 INVITE_INVALID`. An expired, revoked or unknown grant returns `401 PAIRING_REQUIRED`. Malformed requests use `INVALID_REQUEST`. A non-JSON pair request uses `415 UNSUPPORTED_MEDIA_TYPE` and an oversized body uses `413 CONTENT_TOO_LARGE`. A dropped or timed-out relay uses `503 BRIDGE_UNAVAILABLE`. At capacity use `429 CAPACITY_EXCEEDED`. Error bodies have an `error` object with `code` and `message`.

Pairing, revocation and Project readiness are Studio responsibilities. Runtime availability and limits are reported by the running host. Standalone Projects may be granted write access. LAN Projects expose stable, committed read snapshots after synchronization.
