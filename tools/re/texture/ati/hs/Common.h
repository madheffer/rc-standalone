#pragma once
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <cmath>
#include <cfloat>
#include <cassert>
#include <algorithm>
typedef uint8_t CMP_BYTE; typedef int8_t CMP_SBYTE; typedef uint16_t CMP_WORD; typedef uint32_t CMP_DWORD;
typedef uint32_t DWORD; typedef uint8_t BYTE; typedef uint16_t WORD;
typedef float CODECFLOAT;
#define UNREFERENCED_PARAMETER(P) (P)
#include <vector>
#include <string>
#ifndef min
#define min(a,b) (((a) < (b)) ? (a) : (b))
#endif
#ifndef max
#define max(a,b) (((a) > (b)) ? (a) : (b))
#endif
