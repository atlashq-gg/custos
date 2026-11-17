# Authentication

Custos is a public OAuth 2.0 client on the computer of the user ([RFC 8252](https://www.rfc-editor.org/rfc/rfc8252)). It has no client secret. A service decides how the user logs in. Custos only opens a URL in the browser and gets tokens back.

## Login

Custos starts a login when it has no token, or when its token stops working (see [Challenge](#challenge)).

1. Custos makes a PKCE code verifier and an S256 code challenge ([RFC 7636](https://www.rfc-editor.org/rfc/rfc7636)), and a random `state`.
2. Custos opens the `authorization_endpoint` from the discovery document in the default browser, with these parameters:
   - `response_type=code`
   - `client_id` from the discovery document
   - `redirect_uri=custos://oauth2callback`
   - `scope` from the discovery document, joined with spaces
   - `state`, `code_challenge`, and `code_challenge_method=S256`
3. The user logs in. The service redirects the browser to the `redirect_uri`. The operating system gives this URI to Custos.
4. Custos makes sure that `state` is the same. It then sends the code and the code verifier to the `token_endpoint`.
5. Custos keeps the access token, the refresh token, and the expiry time in the credential store of the operating system.

## Requirements for a service

- The authorization endpoint MUST accept the redirect URI `custos://oauth2callback`. This is a private-use URI scheme ([RFC 8252 section 7.1](https://www.rfc-editor.org/rfc/rfc8252#section-7.1)). Custos registers the scheme with the operating system.
- The authorization endpoint MUST require PKCE with S256.
- The token endpoint MUST accept a public client without client authentication.
- The token endpoint SHOULD issue a refresh token, so that the session lasts longer than one access token.
- The `client_id` MAY be an HTTPS URL to a Client ID Metadata Document. The authorization server then gets the client registration from that URL. Custos does not read the document.

A service can point these endpoints at an identity provider, or it can implement them on top of its own login, for example a form. Custos does not see the difference.

## Redirect URI

The redirect URI is the same for all services. A service can thus register it in advance.

- URI: `custos://oauth2callback`.
- The scheme `custos` is short, but it is not a reverse domain name as [RFC 8252 section 7.1](https://www.rfc-editor.org/rfc/rfc8252#section-7.1) recommends. A different application can register the same scheme. PKCE prevents the use of a stolen code by that application.
- The browser can ask the user before it opens Custos. This is normal.

## Access tokens

- Custos sends the access token as `Authorization: Bearer {token}` ([RFC 6750](https://www.rfc-editor.org/rfc/rfc6750)).
- The token is opaque to Custos. Custos does not read its content.
- Before the access token expires, Custos SHOULD get a new one with the refresh token.

## Challenge

A protected endpoint answers `401` with a `WWW-Authenticate: Bearer` header when the token is missing, expired, or not valid.

When Custos gets `401`:

1. If Custos has a refresh token, it gets a new access token one time and sends the request again.
2. If that fails, or if Custos has no refresh token, Custos deletes its tokens. It tells the user that a login is necessary, and it starts the login when the user accepts.

Custos MUST NOT start a browser login without an action of the user, so that a broken service cannot open the browser again and again.

## Logout

Custos deletes its tokens. If the discovery document has a `revocation_endpoint`, Custos SHOULD revoke the refresh token first.
