type VerifiedSkips = std::sync::Arc<Vec<std::collections::HashSet<std::path::PathBuf>>>;
type PreflightPlan = (PreflightResult, VerifiedSkips);

#[cfg(windows)]
fn is_reparse_point(meta: &std::fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
    meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

#[cfg(not(windows))]
fn is_reparse_point(_meta: &std::fs::Metadata) -> bool {
    false
}

include!("preflight/part1.rs");
include!("preflight/part2.rs");
include!("preflight/part3.rs");
include!("preflight/part4.rs");
