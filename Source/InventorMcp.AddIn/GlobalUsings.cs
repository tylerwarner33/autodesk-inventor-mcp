#if !NET9_0_OR_GREATER
// System.Threading.Lock arrived in .NET 9, and Inventor 2025 and 2026 host .NET 8.
// Locking on an object behaves the same, just without the faster implementation.
global using Lock = System.Object;
#endif
