# dmart as an OpenID Connect provider

dmart signs users in to other applications with OpenID Connect, so a suite
built on dmart needs no separate identity provider. In matrix-deploy that
provider is Dex: MAS (Matrix) sends the browser to Dex, and Dex checks the
password against LDAP. With this, MAS, Gitea and any other OIDC relying party
can send the browser to dmart directly. dmart already owns the users and their
passwords, and its LDAP face (`Ldap/`) already replaced Dex's LDAP backend.

Off unless `OIDC_ISSUER` is set.

## The flow

The authorization code flow (OIDC Core §3.1), with PKCE:

```
 relying party ──302──▶ /oidc/authorize ──(signed in already? no: the form)──▶ 302 back with ?code
       │
       └─ back channel: /oidc/token (code + client secret or PKCE verifier)
                          ◀─ id_token (RS256), access_token
                        /oidc/userinfo (access_token) ◀─ claims
```

| Endpoint | |
|---|---|
| `GET /.well-known/openid-configuration` | discovery |
| `GET /oidc/jwks` | the public signing key |
| `GET /oidc/authorize` | single sign-on, or the sign-in form |
| `POST /oidc/authorize` | the form's submission, rate-limited like `/user/login` |
| `POST /oidc/token` | code to tokens |
| `GET`/`POST /oidc/userinfo` | claims for an access token |

- **Single sign-on.** A browser already signed in to dmart (the `auth_token`
  cookie `/user/login` sets, with its session row still live) is not asked
  again. A sign-in through the form opens that same session, so the next
  application skips the form too. `prompt=login` and `max_age` force a fresh
  sign-in; `prompt=none` without a session answers `login_required`.
- **The form** accepts a shortname, a contact email or a hosted mailbox, with
  the password, through the same `UserService.LoginAsync` as `/user/login`:
  the same lockout, Argon2 budget and timing.
- **Directory services gate clients.** A client may list `services`; a user
  must hold one of them (docs/user-directory-fields.md). This is the
  `authorizedService=matrix` filter Dex applied, checked both at sign-in and
  again when the code is redeemed.

## Configuration

```
OIDC_ISSUER="https://id.example.com"          # exactly what relying parties compare
OIDC_SIGNING_KEY_FILE="/var/lib/dmart/oidc-signing-key.pem"
OIDC_CLIENTS_FILE="/etc/dmart/oidc-clients.json"
OIDC_TOKEN_SECONDS=3600
```

- **The issuer** must be https, except on a loopback address. It may carry a
  path when a reverse proxy mounts dmart under one. dmart serves its routes
  at its own root, and discovery advertises them under the issuer.
- **The signing key** is RSA, as PKCS#8 PEM. It is created with mode 0600 on
  first start if the file is missing. Keep it with the database backups:
  - its key id is its RFC 7638 thumbprint;
  - rotating it means replacing the file and restarting, after which tokens
    signed with the old key stop verifying, within `OIDC_TOKEN_SECONDS`.
- **The clients file** is re-read when it changes. A version that fails
  validation keeps the previous one in service and logs why; at startup it
  stops the host.

```json
{"clients": [
  {"client_id": "mas", "client_secret": "<32+ random characters>", "name": "Matrix",
   "redirect_uris": ["https://auth.example.com/upstream/callback/01H8PKNWKKRPCBW4YGH1RWV279"],
   "services": ["matrix"]},
  {"client_id": "gitea", "client_secret": "<…>", "name": "Gitea",
   "redirect_uris": ["https://git.example.com/user/oauth2/dmart/callback"], "services": ["gitea"]},
  {"client_id": "cli", "name": "A native app",
   "redirect_uris": ["http://127.0.0.1:8765/callback"]}
]}
```

- A client **without a secret** is public (a native or browser app) and must
  use PKCE (`S256`). One with a secret authenticates at the token endpoint
  with HTTP Basic or `client_secret_post`; secrets must be at least 16
  characters.
- **Redirect URIs** match exactly. They must be https, or http on a loopback
  address (native apps), and may not carry a fragment.

## Tokens and claims

The **ID token** is RS256-signed, with `typ` `JWT` and these claims:
- `iss`, `sub`, `aud` (the client id), `azp`, `iat`, `exp`, `auth_time`;
- `nonce`, when the request had one;
- the scope's claims, listed below.

The **access token** is an RFC 9068 JWT (`typ` `at+jwt`) whose audience is
the userinfo endpoint. It is **not** a dmart session token. dmart's API
verifies HS256 tokens with its own secret and rejects it, so a relying party
holding it can read the user's claims but cannot act as the user in dmart.

| Scope | Claims |
|---|---|
| `openid` (required) | `sub`: the user's **uuid**, stable across renames |
| `profile` | `name` (display name, else shortname), `preferred_username` (shortname), `locale`, `updated_at` |
| `email` | `email`: the hosted mailbox, else the contact email (what LDAP serves as `mail`); `email_verified` |
| `phone` | `phone_number`, `phone_number_verified` |
| `groups` | `groups`: dmart groups |

The claims appear in the ID token as well as from userinfo, because MAS and
others read them from the token.

## What the design rules out

- **No consent screen.** Every client is configured by an administrator, so
  each is first-party to the deployment.
- **No dynamic registration.** `/oauth/register` (MCP) remains separate.
- **No implicit or hybrid flows**, and no `plain` PKCE.
- **Errors that make the return address untrustworthy are shown, not
  redirected.** These are an unknown client and an unregistered redirect URI
  (RFC 6749 §4.1.2.1). Every other error goes back to the client with
  `state` and `iss` (RFC 9207).
- **Login CSRF.** The form's submission must carry a token matching a cookie
  set with the form (`SameSite=Strict`, path `/oidc`). Credentials posted
  from another site cannot sign this browser in as someone else.
- **Temporary passwords.** An account flagged `force_password_change` is
  stopped with a page asking the user to change their password in dmart
  first.

## Replacing Dex in matrix-deploy

**MAS**, `upstream_oauth2`: point the existing provider at dmart and keep its
ULID, which is the join key into MAS's database.

```yaml
upstream_oauth2:
  providers:
    - id: 01H8PKNWKKRPCBW4YGH1RWV279
      issuer: https://id.example.com
      client_id: mas
      client_secret: "<as in the clients file>"
      token_endpoint_auth_method: client_secret_basic
      scope: "openid profile email"
      claims_imports:
        localpart: { action: require, template: "{{ user.preferred_username }}" }
        displayname: { action: suggest, template: "{{ user.name }}" }
        email: { action: suggest, template: "{{ user.email }}", set_email_verification: always }
```

The subject changes when the provider changes: Dex's `sub` encoded the LDAP
uid and connector id, dmart's is the user's uuid. Existing MAS users are
linked to the old subjects, so a switch needs MAS's upstream links migrated,
or users re-linking on first sign-in. Plan this before switching a live
deployment.

**Gitea**:

```
gitea admin auth add-oauth --name dmart --provider openidConnect \
  --key gitea --secret "<…>" \
  --auto-discover-url https://id.example.com/.well-known/openid-configuration
```

## Not done

- **Refresh tokens.** The relying parties here sign in through the provider
  and keep their own sessions.
- **RP-initiated logout** (`end_session_endpoint`).
- **Key rotation with overlap.** Only one key is published at a time.
- **`at_hash`** in the ID token. It is optional for the code flow.

## Verified

- **Integration tests**, on both engines (`OidcProviderTests`):
  - discovery;
  - a confidential client's full flow, with the ID token verified
    independently against the JWKS;
  - userinfo, and the access token refused by dmart's API;
  - single use of codes, single sign-on, and `prompt=login`;
  - the service gate;
  - public clients and PKCE, including a wrong verifier spending the code;
  - unknown clients and redirect URIs answered with a page, never a redirect;
  - `prompt=none`, login CSRF, and a wrong client secret.
- **Unit tests**:
  - the key: created 0600, a stable id, and rejecting tampering, a foreign
    key and the wrong `typ`;
  - the clients file's validation.
- **`bench/oidc-interop.sh`**, with real relying parties:
  - **oauth2-proxy v7.12.0.** Its go-oidc checks discovery, issuer, audience,
    the signature against the JWKS, expiry and the nonce. Covered: sign-in
    through to the upstream, single sign-on for a second relying party, and
    the service gate.
  - **MAS 1.26.0**, matrix-deploy's pin, configured as matrix-deploy
    configures it for Dex but pointed at dmart. Its homeserver is a stand-in
    that records provisioning calls. MAS:
    - sends PKCE and a nonce;
    - takes the code and ID token;
    - offers to create `@alice` with her hosted mailbox and display name;
    - provisions her: `{"localpart":"alice","set_displayname":"Alice Example","set_emails":["alice@imx.sh"]}`.
