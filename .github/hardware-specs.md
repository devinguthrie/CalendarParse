# Hardware Specs

- **GPU**: NVIDIA RTX 4070 Ti SUPER — 16 GB GDDR6X, 8448 CUDA cores, PCIe x16 Gen4
- **Shared system memory**: 16.3 GB
- **Total available graphics memory**: ~32 GB (dedicated + shared)
- **Constraint**: Models >16 GB VRAM require CPU offload → massive latency + accuracy loss (confirmed Phase 43: qwen2.5vl:32b = 75 min, −23 shifts)
