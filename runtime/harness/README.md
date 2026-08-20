# Harness compatibility path

The legacy TuringDesk Cordis/4317 Harness runtime was removed. Official DeepSeek Harness now runs directly through the bundled `@deepseek-ai/dsh` Web profile.

This directory remains tracked only so older/current QUICK-VERIFY cleanup commands can safely target `runtime/harness/` without failing on a missing Git path. No runtime configuration is loaded from here.
