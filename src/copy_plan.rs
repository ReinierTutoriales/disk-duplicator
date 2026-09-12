pub const MAX_DESTINATIONS: usize = 256;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CopyPlan {
    pub source: String,
    pub dests: Vec<String>,
    pub skip_same: bool,
    pub keep_going: bool,
}

fn normalized_path(path: &str) -> String {
    let trimmed = path.trim();
    let without_extended = if let Some(rest) = trimmed.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{rest}")
    } else if let Some(rest) = trimmed.strip_prefix(r"\\?\") {
        rest.to_owned()
    } else {
        trimmed.to_owned()
    };
    let normalized = without_extended.replace('/', r"\");
    let without_trailing = normalized.trim_end_matches('\\');
    if without_trailing.is_empty() {
        normalized
    } else if without_trailing.len() == 2 && without_trailing.ends_with(':') {
        format!("{without_trailing}\\")
    } else {
        without_trailing.to_owned()
    }
}

#[cfg(windows)]
fn ordinal_eq_ignore_case(a: &str, b: &str) -> bool {
    #[link(name = "kernel32")]
    extern "system" {
        #[link_name = "CompareStringOrdinal"]
        fn compare_string_ordinal(
            string1: *const u16,
            count1: i32,
            string2: *const u16,
            count2: i32,
            ignore_case: i32,
        ) -> i32;
    }
    const CSTR_EQUAL: i32 = 2;
    let a: Vec<u16> = a.encode_utf16().collect();
    let b: Vec<u16> = b.encode_utf16().collect();
    unsafe {
        compare_string_ordinal(a.as_ptr(), a.len() as i32, b.as_ptr(), b.len() as i32, 1)
            == CSTR_EQUAL
    }
}

pub fn same_path(a: &str, b: &str) -> bool {
    let a = normalized_path(a);
    let b = normalized_path(b);
    #[cfg(windows)]
    {
        ordinal_eq_ignore_case(&a, &b)
    }
    #[cfg(not(windows))]
    {
        a == b
    }
}

pub fn append_unique_destinations(
    source: &str,
    existing: &mut Vec<String>,
    selected: impl IntoIterator<Item = String>,
) -> usize {
    let mut added = 0usize;
    for path in selected {
        if existing.len() >= MAX_DESTINATIONS {
            break;
        }
        let path = path.trim().to_owned();
        if path.is_empty() || same_path(&path, source) {
            continue;
        }
        if existing.iter().any(|current| same_path(current, &path)) {
            continue;
        }
        existing.push(path);
        added += 1;
    }
    added
}

impl CopyPlan {
    pub fn new(
        source: impl Into<String>,
        dests: Vec<String>,
        skip_same: bool,
        keep_going: bool,
    ) -> Result<Self, String> {
        let source = source.into().trim().to_owned();
        if source.is_empty() {
            return Err("Selecciona un archivo o carpeta de origen.".to_owned());
        }
        if dests.is_empty() {
            return Err("Agrega al menos un destino.".to_owned());
        }
        if dests.len() > MAX_DESTINATIONS {
            return Err(format!(
                "La copia supera el máximo de {MAX_DESTINATIONS} destinos."
            ));
        }

        let mut clean = Vec::with_capacity(dests.len());
        for dest in dests {
            let dest = dest.trim().to_owned();
            if dest.is_empty() {
                return Err("La copia contiene un destino vacío.".to_owned());
            }
            if same_path(&source, &dest) {
                return Err("El origen no puede ser también un destino.".to_owned());
            }
            if clean
                .iter()
                .any(|current: &String| same_path(current, &dest))
            {
                return Err("La copia contiene destinos duplicados.".to_owned());
            }
            clean.push(dest);
        }

        Ok(Self {
            source,
            dests: clean,
            skip_same,
            keep_going,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plan_trims_and_rejects_invalid_destinations() {
        let plan = CopyPlan::new(
            " C:/Origen/ ",
            vec![" D:/Uno/ ".to_owned(), "E:/Dos".to_owned()],
            true,
            false,
        )
        .unwrap();
        assert_eq!(plan.source, "C:/Origen/");
        assert_eq!(plan.dests, vec!["D:/Uno/", "E:/Dos"]);
        assert!(CopyPlan::new("C:/Origen", vec!["C:/Origen/".to_owned()], true, true).is_err());
        assert!(CopyPlan::new(
            "C:/Origen",
            vec!["D:/Uno".to_owned(), "D:/Uno/".to_owned()],
            true,
            true,
        )
        .is_err());
    }

    #[test]
    fn destination_editing_never_exceeds_plan_limit() {
        let mut existing: Vec<String> = (0..MAX_DESTINATIONS - 1)
            .map(|index| format!(r"D:\Dest{index}"))
            .collect();
        let added = append_unique_destinations(
            r"C:\Source",
            &mut existing,
            [r"E:\One".to_owned(), r"F:\Two".to_owned()],
        );
        assert_eq!(added, 1);
        assert_eq!(existing.len(), MAX_DESTINATIONS);
    }

    #[test]
    fn extended_and_normal_paths_compare_equal() {
        assert!(same_path(r"\\?\C:\Datos\", r"C:/Datos"));
        assert!(same_path(r"\\?\UNC\Servidor\Share\", r"\\Servidor\Share"));
    }

    #[cfg(windows)]
    #[test]
    fn windows_path_comparison_handles_unicode_case() {
        assert!(same_path(r"C:\MÚSICA\Niño", r"c:\música\niño\"));
    }
}
