type VerifiedSkips = std::sync::Arc<Vec<std::collections::HashSet<std::path::PathBuf>>>;
type PreflightPlan = (PreflightResult, VerifiedSkips);

include!("preflight_v2/part1.rs");
include!("preflight_v2/part2.rs");
include!("preflight_v2/part3.rs");
include!("preflight_v2/part4.rs");
