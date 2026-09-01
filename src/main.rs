mod disk_enum;
mod copy_engine;

use disk_enum::enumerate_physical_disks;
use copy_engine::{CopyEngine, CopyJob};
use std::io::{self, Write};

fn main() {
    println!("=== Disk Duplicator ===\n");
    
    // Enumerar discos
    println!("Enumerando discos...");
    let disks = match enumerate_physical_disks() {
        Ok(d) => d,
        Err(e) => {
            eprintln!("Error al enumerar discos: {}", e);
            return;
        }
    };
    
    if disks.is_empty() {
        println!("No se encontraron discos.");
        return;
    }
    
    // Mostrar discos disponibles
    println!("\nDiscos disponibles:");
    for (i, disk) in disks.iter().enumerate() {
        let system_marker = if disk.is_system_disk { " [SISTEMA - NO USAR]" } else { "" };
        println!(
            "{}. PhysicalDrive{} - {} - {} GB - {}{}",
            i + 1,
            disk.index,
            disk.model,
            disk.size_bytes / 1_073_741_824,
            if disk.is_removable { "USB" } else { "Interno" },
            system_marker
        );
    }
    
    // Seleccionar fuente
    print!("\nSelecciona el disco fuente (número): ");
    io::stdout().flush().unwrap();
    let mut source_input = String::new();
    io::stdin().read_line(&mut source_input).unwrap();
    let source_idx: usize = match source_input.trim().parse() {
        Ok(n) if n > 0 && n <= disks.len() => n - 1,
        _ => {
            println!("Selección inválida");
            return;
        }
    };
    
    let source = disks[source_idx].clone();
    
    if source.is_system_disk {
        println!("ERROR: No puedes usar el disco del sistema como fuente");
        return;
    }
    
    // Seleccionar destinos
    print!("Selecciona los discos destino (separados por comas, ej: 2,3,4): ");
    io::stdout().flush().unwrap();
    let mut dest_input = String::new();
    io::stdin().read_line(&mut dest_input).unwrap();
    
    let dest_indices: Vec<usize> = dest_input
        .trim()
        .split(',')
        .filter_map(|s| s.trim().parse::<usize>().ok())
        .map(|n| n - 1)
        .collect();
    
    let destinations: Vec<_> = dest_indices
        .into_iter()
        .filter(|&i| i != source_idx && i < disks.len() && !disks[i].is_system_disk)
        .map(|i| disks[i].clone())
        .collect();
    
    if destinations.is_empty() {
        println!("No se seleccionaron destinos válidos");
        return;
    }
    
    // Confirmar
    println!("\n=== Confirmación ===");
    println!("Fuente: PhysicalDrive{} ({} GB)", source.index, source.size_bytes / 1_073_741_824);
    println!("Destinos: {} disco(s)", destinations.len());
    for dest in &destinations {
        println!("  - PhysicalDrive{} ({} GB)", dest.index, dest.size_bytes / 1_073_741_824);
    }
    
    print!("\n¿Continuar? (s/n): ");
    io::stdout().flush().unwrap();
    let mut confirm = String::new();
    io::stdin().read_line(&mut confirm).unwrap();
    
    if confirm.trim().to_lowercase() != "s" {
        println!("Operación cancelada");
        return;
    }
    
    // Crear job
    let job = CopyJob {
        source,
        destinations,
        buffer_size: 1024 * 1024, // 1MB
        verify: true,
    };
    
    // Ejecutar copia
    println!("\nIniciando copia...");
    let mut engine = CopyEngine::new(job);
    
    match engine.execute() {
        Ok(_) => println!("\nCopia completada exitosamente"),
        Err(e) => eprintln!("\nError en la copia: {}", e),
    }
}
