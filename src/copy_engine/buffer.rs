use std::alloc::{alloc_zeroed, dealloc, Layout};
use std::ptr::NonNull;

pub struct AlignedBuffer {
    ptr: NonNull<u8>,
    layout: Layout,
    len: usize,
}

impl AlignedBuffer {
    pub fn new(size: usize, alignment: usize) -> Self {
        assert!(alignment.is_power_of_two(), "alignment must be power of 2");
        assert!(size % alignment == 0, "size must be multiple of alignment");
        
        let layout = Layout::from_size_align(size, alignment).unwrap();
        let ptr = unsafe {
            let raw = alloc_zeroed(layout);
            NonNull::new(raw).expect("allocation failed")
        };
        
        Self { ptr, layout, len: size }
    }
    
    pub fn as_slice(&self) -> &[u8] {
        unsafe { std::slice::from_raw_parts(self.ptr.as_ptr(), self.len) }
    }
    
    pub fn as_mut_slice(&mut self) -> &mut [u8] {
        unsafe { std::slice::from_raw_parts_mut(self.ptr.as_ptr(), self.len) }
    }
    
    pub fn len(&self) -> usize {
        self.len
    }
}

impl Drop for AlignedBuffer {
    fn drop(&mut self) {
        unsafe {
            dealloc(self.ptr.as_ptr(), self.layout);
        }
    }
}

unsafe impl Send for AlignedBuffer {}
unsafe impl Sync for AlignedBuffer {}
