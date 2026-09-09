---
post_title: "Issue 2582 Worker Xcode Capability A5"
author1: "Jeff Papiez"
post_slug: "issue-2582-worker-xcode-capability-a5"
microsoft_alias: "jpapiez"
featured_image: ""
categories: []
tags:
  - xcode
  - ios
  - simulator
ai_note: true
summary: "Read-only Xcode and simulator capability evidence for Issue #2582."
post_date: "2026-09-09"
---

## Scope and identity

| Field | Value |
| --- | --- |
| Issue | #2582 |
| Ralph job marker | `pf-2582-xcode-capability-20260909-a5` |
| Ralph launch token | `22923a22-a528-4511-b4d7-622dc4ce3f7e` |
| Fence | `136` |
| Actual session identity | `Jeff Papiez` |
| Worker branch | `ralph/pf-2582-xcode-capability-20260909-a5` |
| Base and initial HEAD SHA | `f2e428af37a28206dc5e9b3543bca896ae348c72` |
| Observation time | `2026-09-09T09:09:33.266-07:00` |
| Bounded discovery elapsed time | `0.592878667` seconds of a 120-second limit |

Only discovery commands were run. No simulator was booted, erased, created, or
otherwise mutated. No app was built, tested, installed, or launched.

## Toolchain capability

The selected developer directory is `/Applications/Xcode.app/Contents/Developer`.
`xcodebuild` reports Xcode `26.6`, build `17F113`. `xcrun simctl` successfully
enumerated runtimes and available devices. The available runtime includes the
resolver's approved iOS `26.5` build `23F77`, and the resolver successfully
selected the available shutdown `iPhone 17` with UDID
`1C9C0B0D-E484-484F-9022-75C85B594907`.

These observations prove that the macOS worker has a usable Xcode command-line
toolchain, CoreSimulator discovery capability, the approved runtime, and an
available matching device. They do not prove that the mobile application builds
or tests successfully, because neither operation was in scope.

## Old-UDID or matching-binary experimental prerequisite

This is distinct from toolchain capability. The shared resolver selects the
currently available matching simulator based on the approved runtime and device
preference; it does not require any previously recorded UDID. Its successful
exit status proves the matching runtime/device prerequisite was present at
observation time. A missing old or previously approved UDID would therefore be
an experimental-prerequisite difference, not evidence that Xcode is unavailable.

## Exact command evidence

All five commands completed with exit status `0`. Each stream below is recorded
losslessly as UTF-8 bytes compressed with gzip and encoded as Base64. Decode a
payload with `base64 -D | gzip -dc` on macOS to recover its exact captured
stdout or stderr, including the trailing newline and, for resolver stderr, ANSI
escape bytes.

### `/usr/bin/xcode-select -p`

| Stream | Exact captured value |
| --- | --- |
| Exit status | `0` |
| Stdout, UTF-8 gzip Base64 | `H4sIAPCEoWoC/9N3LCjIyUxOLMnMzyvWj0jOT0nVSywo0HfOzytJzSsp1ndJLUvNyS9ILeICAKXEvGYrAAAA` |
| Stderr, UTF-8 gzip Base64 | `H4sIAPCEoWoC/wMAAAAAAAAAAAA=` |

### `/usr/bin/xcodebuild -version`

| Stream | Exact captured value |
| --- | --- |
| Exit status | `0` |
| Stdout, UTF-8 gzip Base64 | `H4sIAPCEoWoC/4tIzk9JVTAy0zPjcirNzElRKEstKs7Mz1MwNHczNDTmAgB8ITxrIAAAAA==` |
| Stderr, UTF-8 gzip Base64 | `H4sIAPCEoWoC/wMAAAAAAAAAAAA=` |

### `/usr/bin/xcrun simctl list runtimes -j`

| Stream | Exact captured value |
| --- | --- |
| Exit status | `0` |
| Stdout, UTF-8 gzip Base64 | `H4sIAPCEoWoC/9WcXU/jOBSG7/kVUa86EinYsUPLXXd3ZrXSItB0di9mGK1CY6aW8lE5CVqE5r+P45Y2bUFg5xjsG9Q2Pifmyev3nLapH46CYCCaouY5qwbBefBNvhAED+qvPMSr6V3Cs+QmY+3RWjTs+PHYHRMVL4v29QGOR3RwvI36q6iZKJKsPXibZNU26qbhWboTGn06O9vGVs1yWYqapVMxX/CazetGdKemBiUij8lg/fz7Yewf7I7P2Zf75X7kw+aRmkqRZuwqqRdqItcnf/MbkYj76xMZz7JyycT1ye+lYDOeN1lSl/LplShvecYqNebxHNcn/GpRFixAZ4EcMKp4nqqjtTy6+dfUOYskVyQHOxG7Y3jK5AW55UyokfMyHyXLZcZGO3MZyUfbOYxW+UJ0Fh7kW4oybeb1pyTn2X3n5IPNoJ/Hb0souEj+16fURkGTCg9yukWL6WFikHyYu2CmXOiAkcPhwBwkc0sxeoKB1IvDVGJtX46BfTl23JdjI1+OLfhy7IEvx3q+HEP6csxcBqPHBRKL24srayrdlSVDYJfVQUKnIFFtj6bAHk0d92hq5NHUgkdTDzya6lGCpOO2hnStiEJbEXXdioi2FRFgKyKOWxExsiJiwYqIB1ZE9ChB0nFbQ7pWRKCtiDhuRbOPwTASafCDFUwkNS+LDzrAngiHgzf7GMrc4Ta3w1qLtB09Anb0yHFHj4wcPbLg6JEHjh7pUYKk47SGcl5wTQG1IaDqOUzoFCSsbUUY2Iqw41aEjawIW7Ai7IEVYT1KkHSc1pC2FWFoK8KOW1HbHeKiV3O5Fw7aXIYyeae7DB1WG9L2dATs6chxT0dGno4seDrywNORHiVIOk5SSVIlBNnc8WK+CIYX9EMwRPGfv73WsJ5PAMIuSZWo1unDCxq2uV9GmaTvD9KcoC102Gl0qK8GkV0NIl80iIw1iCxpEHmnQdJXg8SqBok3GiQ9CFpCN/ajgpC+pZhYLcXEm1JMehC0hM5ZCU65MCS3HwlFTubtkHOYGjKmhixRQ25TG05RrIFKDQfj85r72t5/AUbGCzCyswAjHxZgZLwAIzsL0Atq2JgatkMN+7BCsfEKxXZWqKPU2k/sWwtXvzjSYLYbB0WszSprwCt/1PVeLexwvRRkH0/qRff7AKN3Bi+ms/F+S56p+12D2+8ZnicERfoNEI/dJoxHk0c2MYioX0wIyhyHq7OFsV/Cfp4SHO83Ae2svIfo1BTsQSgUyTav1o2E79VQDakpu6eiIRsr6gHBVZNkvK6fDAdttWIPIO4VbrO7g1/MY6MB0L1b2IkqRMGqEH2LKuSDDQwnxgVoYofhxJfyQ3qVH2Kv/Ox3944qb2ysvLEdduHYB25KPcaV5qloUO3tFpbQ5eaH9mt+qMXmZ692hF60P2b3r76YB7I4b/ofgztanWiBCFgLZPFTpk4P5Ekx2lMgqmoQJe/lsaJkT4Tb873R85msCbdfJTvYEe/1M/u82gRwxC9nIY7Dzm5+SznmthT56oSXs0FnLz/96/dvmTW5umKXs//U3n+al3090VWGoN14sL2s6z0Mt3Nbv/C5LGtXJtfGFbW8IO1AVpWNmHdi1Ew308+Sqv6nSn4oTT7s73yo9k08lZfpdBKe0i+InJPxOYm+Pkphs+A6sl7PZzWkVcr3o59HvwBrwS1XAFIAAA==` |
| Stderr, UTF-8 gzip Base64 | `H4sIAPCEoWoC/wMAAAAAAAAAAAA=` |

### `/usr/bin/xcrun simctl list devices available -j`

| Stream | Exact captured value |
| --- | --- |
| Exit status | `0` |
| Stdout, UTF-8 gzip Base64 | `H4sIAPCEoWoC/8WXQW/bIBiG7/0VVk6d1C8BAwZ2A4ynSa1WLdtp2cFtWOMpiSMn6dZV/e/DbrVmqjXZScMOUUjwC6/xY76X+5MoGkzdbXHt1oPobXTvf/t/rsvFMF+t5m5oysqNi8V2nm/KauhbH7fLTbFww+LDGOIEWK360qiiJ3UzwjTf5Jf5ZlZ3Dyajz2tXrSej76t8Vbhfk9F5cVXl1d1klLpbNy9XrpqM/pqq6aldTUZISoS4ZWCJwkClNKCNscASZJBliFFOJqN6wsHZSwPj4perTWBBCOcy3rlkXt50sXhe3qxfuOtiatfOdlpMm4n6Cou1us2LeX41b25jU23d7l02i/TpbuXeT51/MN8KVzXT/OMJpn80w+JyVi4dYA6XVbk763qTb5r5BuPZdjMtfyx3e5f54rHzUR9hHtX6pwsezo6Kg2YiJpQnoDlKgGKsQGLDQStjtVAKZTgNjkMXU6049BWGwwEu8p+HIhHVY4TBIs1EZji1oBCzQFFMfEvFIEimBLGMIIWCY9HFVCsWfYVhsHCH8OACgYCThMUms2BSGwNl/o3SSFOItRYJ49JkGQsOQhdTrSD0FYYAQRXV/iDU4kAgGGmQRilYKij4TwYSxTFwZgTTTFKJeHgQOphqB6GnMMyOcMiGEAgDSqQ1hlBfV5UEmlAJ0vhWzDlHKSMx4jI4Bl1MtWLQV3hkDPJpExUwgWJ5PYMLBjh+p/eiIp82ieFpqOj0gr0JlSiJyYjkyAdxIfyaphykEAa41tbIWPpk/h8SZQdT7YmypzAQIfj1CMHBCcHCplxlGhTRDGhKCMjUF2XLGc0UowxRHb6UdDDVXkp6Co9PyKJYFqAOOIZ6NOoxolP1ePAIBUYi/K6b+ZwusfbbMSMJyMwf7qlIM6K4tlaHzxhdTLWC0Vd4fDB8WnwuLnRfNPwoO3WFhoJD2lhkmilgqU9rNKXGLyz2BRtJa7mJrY/zweHoYqoVjr7CQHDgV4EDB4fDYMHTBEnAiUZAtfKRziAMGccp8ysqEiSCw9HFVCscfYUB4MDJvkT4IpI8Y9B8fz2pWw8nvwF1YcbtMBcAAA==` |
| Stderr, UTF-8 gzip Base64 | `H4sIAPCEoWoC/wMAAAAAAAAAAAA=` |

### `scripts/ci/resolve-ios-simulator.sh --udid`

| Stream | Exact captured value |
| --- | --- |
| Exit status | `0` |
| Stdout, UTF-8 gzip Base64 | `H4sIAPCEoWoC/zN0tnQ2cDJw0XU1sTDRBWI3XUsDIyNdc1NnC1MnU0sTSwNzLgDvOcQzJQAAAA==` |
| Stderr, UTF-8 gzip Base64 | `H4sIAPCEoWoC/5OONrA2Nsl91LLz/Y5+BYXQ4sy8dIVM/2CF4szc0pzEkvwiK4XMgIz8vFQFQ3MFDZCMkZmeqY6CkbGbubk1VE5TIdrQ2dLZwMnARdfVxMJEF4jddC0NjIx0zU2dLUydTC1NLA3MY6WjDXK5ABWUs+tzAAAA` |

