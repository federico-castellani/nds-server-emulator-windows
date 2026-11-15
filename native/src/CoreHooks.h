// Callbacks from the melonDS Platform layer into ndscore.cpp.
#pragma once

#include <types.h>

namespace ndscore_hooks
{
void OnSaveWritten();
void OnFirmwareWritten();
void OnStopSignal();
int NetSend(melonDS::u8* data, int len);
int NetRecv(melonDS::u8* data);
}
