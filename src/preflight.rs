type VerifiedSkips = std::sync::Arc<Vec<std::collections::HashSet<std::path::PathBuf>>>;
type PreflightPlan = (PreflightResult, VerifiedSkips);

include!("preflight/part1.rs");
include!("preflight/part2.rs");
include!("preflight/part3.rs");
include!("preflight/part4.rs");
