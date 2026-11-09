## Summary

<!-- What does this pull request change, and why? Link related issues (e.g. "Fixes #123"). -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Hardware support (new or corrected sensors)
- [ ] Refactoring / code quality
- [ ] Documentation / build / CI

## Testing

<!-- How did you test this? For hardware-related changes, list the hardware you tested on (CPU, GPU, motherboard, storage) and whether OmniSpec ran as administrator. -->

## Checklist

- [ ] `dotnet build OmniSpec.slnx` succeeds without new warnings.
- [ ] `dotnet test OmniSpec.slnx -c Debug --filter "Category!=Hardware"` passes.
- [ ] New logic is covered by tests; tests that need real hardware are tagged `[Trait("Category", "Hardware")]`.
- [ ] No fabricated values: every metric reports a real reading or an honest state (`Unsupported`, `Unavailable`, `PermissionRequired`, `Error`) with its source.
- [ ] No vendor-specific raw SMART attribute is interpreted without a verified mapping.
- [ ] No telemetry or network calls, and no hardware control (fans, voltages, overclocking) were added.
- [ ] Mock data is only reachable in Debug builds and is labelled MOCK.
- [ ] Documentation updated where needed (README, CHANGELOG, third-party notices for new or changed dependencies).
- [ ] I have read [CONTRIBUTING.md](../CONTRIBUTING.md) and agree to license my contribution under the MIT License.
