# Code Signing

Hot Corners is code-signed with **Azure Trusted Signing** (now branded **Azure
Artifact Signing**) so Windows SmartScreen stops warning when the installer is
downloaded and when the tray launches at sign-in.

The release workflow (`.github/workflows/release.yml`) already contains the
signing steps. They stay dormant until the credentials below are configured,
then activate automatically on the next build — no code changes needed.

Hot Corners reuses the same Trusted Signing account and certificate profile as
[Swoosh](https://github.com/bwya77/Swoosh): the cert is bound to the
publisher's individual identity (`CN=Bradley Wyatt`), and one Public Trust
profile can sign any number of apps from that identity.

## Configured resources

| Resource | Value |
| --- | --- |
| Trusted Signing account | `swoosh-signing` |
| Region / endpoint | North Central US — `https://ncus.codesigning.azure.net/` |
| Certificate profile | `swoosh` (Public Trust, Individual Developer) |
| Publisher identity | `CN=Bradley Wyatt, O=Bradley Wyatt, L=Geneva, S=IL, C=US` |
| CI auth | App Registration `swoosh-signing-ci` with the *Trusted Signing Certificate Profile Signer* role |

The workflow signs the launched `*.exe` for both architectures (`win-x64`,
`win-arm64`) before packaging into installers, then signs each installer
executable so the file users actually double-click is signed too.

## Why this and not an EV certificate

| Option | Cost | SmartScreen | Notes |
| --- | --- | --- | --- |
| Unsigned | $0 | Warns every download | Prior state |
| **Trusted Signing (this)** | **~$10/mo** | Clears once reputation builds | Best fit for an indie app |
| OV certificate | $300–500/yr | Same reputation model | More setup, secrets on disk |
| EV certificate | $300–700/yr | **Instant trust** | Hardware token / cloud HSM |

Trusted Signing is OV-equivalent: SmartScreen reputation is tied to the verified
publisher identity and builds over a few weeks of clean installs. It does **not**
grant instant trust — only an EV certificate does that. Because Swoosh and Hot
Corners share the same publisher identity, reputation Swoosh has earned
contributes to Hot Corners' standing too.

## Eligibility

This project uses the **Individual Developer** identity-validation path, which is
available to individual developers in the **USA and Canada**. (The *Organization*
path is available in the USA, Canada, EU, and UK — that's a separate option.)

The certificate publisher name will be the developer's **legal name**, not a
company name.

## Configuring a fresh repository

If signing was set up from scratch (see Swoosh's `SIGNING.md` for the full
one-time identity-validation walkthrough), only the per-repository config is
needed. In **Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `AZURE_TENANT_ID` | App Registration tenant ID |
| Secret | `AZURE_CLIENT_ID` | App Registration client ID |
| Secret | `AZURE_CLIENT_SECRET` | App Registration client secret |
| Variable | `TRUSTED_SIGNING_ENDPOINT` | e.g. `https://ncus.codesigning.azure.net/` |
| Variable | `TRUSTED_SIGNING_ACCOUNT` | the account name |
| Variable | `TRUSTED_SIGNING_PROFILE` | the certificate profile name |

The three variables are already set on this repo. The three secrets must be
pasted in once.

## How the workflow guards signing

The job sets `HAS_SIGNING: ${{ secrets.AZURE_CLIENT_ID != '' }}` and the sign
steps run only `if: env.HAS_SIGNING == 'true'`. With no credentials configured
the steps are skipped and unsigned builds publish exactly as before.

The workflow deliberately does **not** sign the bundled .NET / WindowsAppSDK
runtime DLLs — only the launched executables matter for SmartScreen, and signing
the whole runtime pushes the step past 15 minutes for marginal benefit.

## Verifying a signed build

After a signed release, download the installer or the extracted exe and check it:

```powershell
Get-AuthenticodeSignature .\HotCornersSetup-0.4.1-win-x64.exe |
    Format-List Status, SignerCertificate
```

`Status` should be `Valid` and the signer certificate subject should show the
publisher (legal name) identity.

You can also verify the build provenance attestation:

```powershell
gh attestation verify .\HotCornersSetup-0.4.1-win-x64.exe `
    --repo bwya77/Windows-Hot-Corners
```
