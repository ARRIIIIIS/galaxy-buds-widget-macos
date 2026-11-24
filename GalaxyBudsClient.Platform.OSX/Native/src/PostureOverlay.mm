//
// PostureOverlay.mm
// Full-screen blur overlay used by the posture monitor.
//
// Creates one borderless, click-through window per screen and blurs whatever is
// behind it. Two mechanisms are combined:
//   * NSVisualEffectView (behindWindow + fullScreenUI) for the base material
//   * CGSSetWindowBackgroundBlurRadius (private) for a controllable radius
// The private call is what makes the blur strength ramp smoothly; if it is
// unavailable we fall back to fading the material in.
//
// Exposed to .NET via libPostureOverlay.dylib.
//

#import <Cocoa/Cocoa.h>
#import <dlfcn.h>

typedef uint32_t (*CGSMainConnectionIDFn)(void);
typedef int32_t (*CGSSetWindowBackgroundBlurRadiusFn)(uint32_t, uint32_t, int32_t);

static NSMutableArray<NSWindow *> *gWindows = nil;
static NSMutableArray<NSVisualEffectView *> *gBlurViews = nil;
static BOOL gShown = NO;

static int32_t gRadius = 0;
static int32_t gPendingRadius = 0;

static CGSMainConnectionIDFn gGetConnectionID = NULL;
static CGSSetWindowBackgroundBlurRadiusFn gSetBlurRadius = NULL;

static void ResolvePrivateAPIs(void)
{
    if (gSetBlurRadius != NULL) {
        return;
    }

    // The symbols are global once SkyLight/CoreGraphics is loaded; try the
    // already-loaded image first, then the private framework explicitly.
    void *handle = dlopen(NULL, RTLD_LAZY);
    gGetConnectionID = (CGSMainConnectionIDFn)dlsym(handle, "CGSMainConnectionID");
    gSetBlurRadius = (CGSSetWindowBackgroundBlurRadiusFn)dlsym(handle, "CGSSetWindowBackgroundBlurRadius");

    if (gGetConnectionID == NULL || gSetBlurRadius == NULL) {
        void *skyLight = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_LAZY);
        if (skyLight != NULL) {
            if (gGetConnectionID == NULL) {
                gGetConnectionID = (CGSMainConnectionIDFn)dlsym(skyLight, "CGSMainConnectionID");
            }
            if (gSetBlurRadius == NULL) {
                gSetBlurRadius = (CGSSetWindowBackgroundBlurRadiusFn)dlsym(skyLight, "CGSSetWindowBackgroundBlurRadius");
            }
        }
    }
}

static void ApplyRadiusToWindows(void)
{
    if (gWindows == nil) {
        return;
    }

    ResolvePrivateAPIs();

    if (gGetConnectionID != NULL && gSetBlurRadius != NULL) {
        uint32_t cid = gGetConnectionID();
        for (NSWindow *window in gWindows) {
            gSetBlurRadius(cid, (uint32_t)[window windowNumber], gRadius);
        }
        // CGSSetWindowBackgroundBlurRadius blurs whatever is *behind* the window, so the window's
        // own content must stay invisible. Keeping the material at alpha 0 is what makes this look
        // like an out-of-focus camera instead of a frosted sheet laid over the screen.
        for (NSVisualEffectView *view in gBlurViews) {
            view.alphaValue = 0.0;
        }
    } else {
        // Fallback: fade the material in instead of changing the radius. The sqrt curve makes the
        // first few degrees of blur visible; a linear fade stays imperceptible until half strength.
        CGFloat normalized = (CGFloat)gRadius / 64.0;
        CGFloat alpha = MIN(1.0, sqrt(normalized) * 1.2);
        for (NSVisualEffectView *view in gBlurViews) {
            view.alphaValue = alpha;
        }
    }
}

static void DestroyWindows(void)
{
    if (gWindows == nil) {
        return;
    }

    for (NSWindow *window in gWindows) {
        [window orderOut:nil];
    }

    [gWindows removeAllObjects];
    [gBlurViews removeAllObjects];
    gShown = NO;
}

static void CreateWindows(void)
{
    if (gWindows == nil) {
        gWindows = [NSMutableArray array];
        gBlurViews = [NSMutableArray array];
    }

    for (NSScreen *screen in [NSScreen screens]) {
        NSRect frame = [screen frame];

        NSWindow *window = [[NSWindow alloc] initWithContentRect:frame
                                                       styleMask:NSWindowStyleMaskBorderless
                                                         backing:NSBackingStoreBuffered
                                                           defer:NO];
        [window setOpaque:NO];
        [window setBackgroundColor:[NSColor clearColor]];
        // Screensaver level so the blur also covers the menu bar, the Dock and full-screen apps.
        [window setLevel:NSScreenSaverWindowLevel];
        [window setCollectionBehavior:NSWindowCollectionBehaviorCanJoinAllSpaces |
                                      NSWindowCollectionBehaviorFullScreenAuxiliary |
                                      NSWindowCollectionBehaviorStationary];
        [window setIgnoresMouseEvents:YES];
        [window setHasShadow:NO];
        [window setReleasedWhenClosed:NO];

        NSVisualEffectView *blurView =
            [[NSVisualEffectView alloc] initWithFrame:NSMakeRect(0, 0, frame.size.width, frame.size.height)];
        [blurView setBlendingMode:NSVisualEffectBlendingModeBehindWindow];
        [blurView setMaterial:NSVisualEffectMaterialFullScreenUI];
        [blurView setState:NSVisualEffectStateActive];
        [blurView setAlphaValue:0.0];
        [blurView setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];

        [window setContentView:blurView];
        [window orderFrontRegardless];

        [gWindows addObject:window];
        [gBlurViews addObject:blurView];
    }

    gShown = YES;
    ApplyRadiusToWindows();
}

// MARK: - Public C API

extern "C" {

void PostureOverlayShow(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        if (gShown) {
            return;
        }
        CreateWindows();
    });
}

void PostureOverlayHide(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        DestroyWindows();
    });
}

void PostureOverlaySetRadius(int32_t radius)
{
    gPendingRadius = MAX(0, MIN(radius, 96));
    dispatch_async(dispatch_get_main_queue(), ^{
        gRadius = gPendingRadius;
        if (gRadius > 0 && !gShown) {
            CreateWindows();
        }
        ApplyRadiusToWindows();
    });
}

/// Rebuilds the overlays after a display change. Called from .NET when needed.
void PostureOverlayRefreshScreens(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!gShown) {
            return;
        }
        DestroyWindows();
        CreateWindows();
    });
}

/// Returns 1 when the private blur API is available, 0 otherwise.
int32_t PostureOverlayHasNativeBlur(void)
{
    ResolvePrivateAPIs();
    return (gGetConnectionID != NULL && gSetBlurRadius != NULL) ? 1 : 0;
}

} // extern "C"
