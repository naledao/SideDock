#pragma once

// Tracing GUID: 1D37EF59-31CB-4D8A-9B97-0C8E7569C6D6
#define WPP_CONTROL_GUIDS                                                   \
    WPP_DEFINE_CONTROL_GUID(                                                \
        SideDockIddTraceGuid, (1d37ef59,31cb,4d8a,9b97,0c8e7569c6d6),        \
        WPP_DEFINE_BIT(SIDEDOCK_IDD_ALL)                                    \
        WPP_DEFINE_BIT(TRACE_DRIVER)                                        \
        WPP_DEFINE_BIT(TRACE_DEVICE)                                        \
        WPP_DEFINE_BIT(TRACE_ADAPTER)                                       \
        WPP_DEFINE_BIT(TRACE_MONITOR)                                       \
        WPP_DEFINE_BIT(TRACE_SWAPCHAIN)                                     \
        )

#define WPP_FLAG_LEVEL_LOGGER(flag, level) WPP_LEVEL_LOGGER(flag)

#define WPP_FLAG_LEVEL_ENABLED(flag, level) \
    (WPP_LEVEL_ENABLED(flag) && WPP_CONTROL(WPP_BIT_ ## flag).Level >= level)

#define WPP_LEVEL_FLAGS_LOGGER(level, flags) WPP_LEVEL_LOGGER(flags)

#define WPP_LEVEL_FLAGS_ENABLED(level, flags) \
    (WPP_LEVEL_ENABLED(flags) && WPP_CONTROL(WPP_BIT_ ## flags).Level >= level)

//
// begin_wpp config
// FUNC Trace{FLAG=SIDEDOCK_IDD_ALL}(LEVEL, MSG, ...);
// FUNC TraceEvents(LEVEL, FLAGS, MSG, ...);
// end_wpp
//

#define MYDRIVER_TRACING_ID L"SideDock\\UMDF\\SideDock.Idd v0.1"
