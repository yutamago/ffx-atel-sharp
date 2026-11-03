<!-- SPDX-License-Identifier: MIT -->
# Hand-written names

One file per script, `<category>/<script>.toml` (`events/kami0000.toml`,
`monsters/m209.toml`, `battles/kino00_00.toml`): names for its workers by
number, its entries by `"worker.entry"` and its variables by
`"Storage:offset"`. The decompiler embeds and applies them. Our own names
only, keyed by numbers: no game code or text. Format and rules:
[docs/source-repo.md](../../docs/source-repo.md#names).

```toml
[workers]
18 = "LightningTowers"

[entries]
"18.2" = "StrikeNearPlayer"

[variables]
"Event:0x0012" = "DodgeCount"
```
