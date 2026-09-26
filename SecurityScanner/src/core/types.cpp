#include "types.h"

namespace scan {

const char* VerdictToString(Verdict verdict)
{
    switch (verdict) {
    case Verdict::Clean: return "clean";
    case Verdict::Suspicious: return "suspicious";
    case Verdict::Malware: return "malware";
    case Verdict::Error: return "error";
    }
    return "unknown";
}

} // namespace scan
