// Math function definitions. Companion to math.h — the prototypes there
// declare what's available; this file provides the implementations.

#include "math.h"

float square(float x) {
    return x * x;
}

double dsum(double a, double b) {
    return a + b;
}

// Sum n ints starting at p. Demonstrates pointer arithmetic over int*.
int sum_ints(int* p, int n) {
    int s = 0;
    int i = 0;
    while (i < n) {
        s = s + *(p + i);
        i = i + 1;
    }
    return s;
}
