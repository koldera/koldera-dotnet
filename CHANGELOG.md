# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/) (0.x releases may change the API).

## 0.1.0 - Unreleased

Initial release of `Koldera.Sdk`, for koldera-core 0.1.

- `KolderaClient` over `HttpClient`, for .NET 8 and .NET 10; trimming and AOT compatible, nullable annotated, XML
  documentation in the package, deterministic build with Source Link and a symbol package. Every resource is an
  interface so the client can be faked.
- `services.AddKoldera(...)` registers `IKolderaClient` as a typed client of `IHttpClientFactory`, from code or from a
  configuration section, with options validated on start. `BaseUrl` and `Token`, or `KOLDERA_URL` and `KOLDERA_TOKEN`.
- Resources: `Pdf` and `HtmlToPdf` jobs (`analyze` and `fix`, `generate_analyze` and `generate_fix`), `Web` scans,
  `Documents` (typed structured documents, image uploads, raw JSON escape hatch), `Jobs` (get, wait, findings, actions,
  artifacts, reading-order evidence and approved corrections), `Artifacts`, `Uploads`, `ClientSessions` (scoped,
  short-lived browser sessions minted by your backend), `InfoAsync` and `HealthAsync`.
- `Wait` (sent as `Prefer: wait=N`) and `UntilFinished` polling on every creation call; `Jobs.WaitAsync` treats any
  state other than queued or running as final and honours `Retry-After`.
- Open enums: unknown states, kinds, modes and problem codes from a newer koldera-core stay plain strings.
- Typed exceptions per HTTP status with the problem `Code`, per-problem `Errors` and `RetryAfter`.
- Idempotency keys on creation calls; network failures are repeated with the same key.
- Artifact downloads verify size and SHA-256 before returning or writing a byte (`DownloadToFileAsync` writes a
  temporary file and moves it into place without overwriting).
- Signed upload and download addresses use a separate SDK-owned client with its own `StorageTimeout`; the caller's
  `HttpClient` is never modified and never sees them. With `AddKoldera` all clients of a service provider share one
  storage client.
- The exception class follows the HTTP status; the problem `code` of the body is kept whenever it is a string.
- Apache-2.0 licence; CI with contract, licence, secret and documentation-example checks; a release workflow with NuGet
  trusted publishing.
