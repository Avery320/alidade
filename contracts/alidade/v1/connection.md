# Torquetum local geometry API

Torquetum serves native clients on an ephemeral `http://127.0.0.1:<port>` listener while the paired Project is running. Clients use the advertised numeric address and disable redirects, cookies and proxies. The listener validates the local peer and exact Host. Browser requests with an Origin are rejected on the native HTTP routes. Responses use `Cache-Control: no-store`.

This contract has two version numbers: the connection protocol / HTTP API version and the exchange collection's `schemaVersion`. The [connection examples](fixtures/connection.json) contain only synthetic IDs and credentials.

## Native HTTP API

| Method and path | Input | Response |
| --- | --- | --- |
| `POST /v1/pair` | A UTF-8 JSON body of at most 4 KiB with `invite`, `clientName`, `apiVersion` and `collectionVersions`. | The Project identity and capabilities, `grantId`, `accessToken`, and UTC `expiresAt`. |
| `GET /v1/project` | `Authorization: Bearer <accessToken>`. | The current Project identity, readiness and capabilities for that grant. |
| `GET /v1/asterism/ports` | Bearer. | `200` with `{items}`. Each item has `id`, `name` and `direction` (`input` or `output`). Inputs also include `etag`, even before the first write. |
| `GET /v1/asterism/outputs/{portId}` | Bearer. | `200` with the completed geometry collection. A result that is pending, incomplete or unavailable returns `409 OUTPUT_NOT_READY`. |
| `PUT /v1/asterism/inputs/{portId}` | Bearer with write permission, `X-Alidade-Request-Id` UUID, a single strong `If-Match` from the list or a committed receipt, and the raw geometry collection as JSON. | `202` with a relative receipt URL or `412 STALE_REVISION`. Missing `If-Match` gives `428 PRECONDITION_REQUIRED`. |
| `GET /v1/requests/{requestId}` | The same grant as the mutation. | `200` with `{requestId,state,result?}` or `404 REQUEST_UNKNOWN`. |

An input or output is an exchange node explicitly added to the Asterism graph. Its ID survives renames and project saves. Use the ID as one percent-encoded path segment. A missing port or a port in the wrong direction returns `404 PORT_NOT_FOUND`. Input writes replace the running snapshot and queue graph evaluation. Reopening a project restores the nodes and their connections. Pair and send again to supply input data to the new runtime. Asterism can preview the results and bake them as Station geometry through its existing UI. Clients using the earlier unpublished `/v1/geometry` routes must migrate to the Asterism ports.

The corresponding bridge route names are `asterism-ports`, `asterism-output`, `asterism-input` and `receipt`. Geometry bodies have a 32 MiB transport limit. Use the paired Project's `limits.maxBodyBytes` for the host's effective limit. Output reads allow up to 60 seconds. The normal relay limit is 15 seconds.

Clients include collection version 1 at pairing and may also offer version 2. The grant's `collectionVersions` and `kinds` are authoritative. Both versions use the same HTTP routes. Snapshots that can be represented by the six v1 kinds use schema version 1. A snapshot containing an exact STEP item uses [collection version 2](../v2/geometry-collection.md) and requires that version in the grant. A v1 grant receives `422 UNSUPPORTED_GEOMETRY` when reading a CAD-only item or attempting a v2 write. An admitted write reports that error in its receipt.

`202` records admission. Follow the same host's relative `/v1/requests/{requestId}` to read the receipt. A receipt is `pending`, `committed` or `rejected`. A committed result contains `httpStatus: 200`, `portId` and the new `etag`. It confirms that the input was accepted for evaluation. Read the output to get the completed geometry. A rejected result contains `httpStatus` and an `error` object.

For a retry, resend the same UUID, URL, conditions, media type and exact body bytes. The server returns the same receipt URL for an admitted repeat. Reusing the UUID for different input gives `409 REQUEST_ID_CONFLICT`. Pre-admission busy and capacity responses leave the ID available for the same request. A terminal rejection requires a new, explicit operation.

An invite and an access token both have the form `<instance UUID>.<base64url secret>`. The secret is 32 bytes with no base64 padding. The server uses the UUID to route the request. Studio validates the full credential against the running Project. A copied invitation is `ALIDADE1.` followed by unpadded base64url UTF-8 JSON containing `endpoint` and `invite`.

A consumed, expired or unknown invite returns `401 INVITE_INVALID`. An expired, revoked or unknown grant returns `401 PAIRING_REQUIRED`. Malformed requests use `INVALID_REQUEST`. A non-JSON pair request uses `415 UNSUPPORTED_MEDIA_TYPE` and an oversized body uses `413 CONTENT_TOO_LARGE`. A dropped or timed-out relay uses `503 BRIDGE_UNAVAILABLE`. At capacity use `429 CAPACITY_EXCEEDED`. Error bodies have an `error` object with `code` and `message`.

Pairing, revocation and Project readiness are Studio responsibilities. Runtime availability and limits are reported by the running host. Standalone Projects may be granted write access. LAN Projects expose completed outputs for reading after synchronization.

## Internal Studio bridge

Studio obtains an endpoint, a WebSocket URL and a one-use ticket from its host. Web tickets are bound to the requesting origin. A Desktop ticket can be issued for the trusted WebView's actual origin, including `null` when the WebView uses an opaque origin. A ticket expires after 30 seconds. The first message must be an `attach` control message within 3 seconds. The server acknowledges an accepted ticket with `attached`.

The attached socket registers each running Project by its runtime UUID and waits for `registered`. The `unregister` control message removes that runtime's route. Closing the socket removes all its routes and terminates its outstanding relays. Each newly registered Project lifetime has a new UUID.

Control messages use text frames. Each binary request or response frame contains a four-byte unsigned big-endian JSON header length, the UTF-8 header, and the original HTTP body bytes. The header limit is 16 KiB. A body-less message ends after the header. A request's `bearer` field contains the complete credential following `Bearer `. Both port routes carry a `portId`. Input writes also carry `mediaType`, `requestId`, `bodySha256` and `ifMatch`. The server accepts a response only from the socket which received the request. Approved response headers are `Content-Type`, `Location` and `Retry-After`.
