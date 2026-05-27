// Math utilities for the C-minus demo. Strict C99 — also accepted by gcc.
// Uses the classic header guard pattern with #ifndef / #define / #endif.
// On re-inclusion, the second pass sees _CMINUS_MATH_H defined and skips
// to #endif, so duplicate-definition errors are avoided.

#ifndef _CMINUS_MATH_H
#define _CMINUS_MATH_H

// Forward declarations (prototypes). Real C accepts identifier-free param
// declarations in prototypes — we list names here for documentation only.
float  square(float x);
double dsum(double a, double b);
int    sum_ints(int* p, int n);

#endif
