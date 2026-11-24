//
// BudsWidget.mm
// Native macOS Liquid Glass desktop widget for Galaxy Buds Manager.
//
// Layout (344x162):
//   - Clear Liquid Glass + crisp 1px top-layer specular rim highlight
//   - Header: connection dot, earbuds icon, device name, connect/hide
//   - Weakened battery line: compact inline L / R / Case indicators
//   - Main control: 4-mode Noise Control segmented capsule (Off / Adaptive / ANC / Ambient)
//   - Connect animation: center hero earbuds + ripple -> shrinks into top-left icon
//

#import <Cocoa/Cocoa.h>
#import <QuartzCore/QuartzCore.h>

typedef void (*BudsWidgetActionCallbackFn)(int32_t action);

static const CGFloat kWidgetWidth = 344.0;
static const CGFloat kWidgetHeight = 162.0;
static const CGFloat kCornerRadius = 24.0;

static NSString *const kPrefOriginX = @"BudsWidgetOriginX";
static NSString *const kPrefOriginY = @"BudsWidgetOriginY";

@interface BudsWidgetPanel : NSPanel
@end

@implementation BudsWidgetPanel
- (BOOL)canBecomeKeyWindow { return NO; }
- (BOOL)canBecomeMainWindow { return NO; }
@end

// Hold-to-drag root view: requires 0.25s press in the top header region before
// handing off to WindowServer's native performWindowDragWithEvent: for 120fps tracking.
// Also drives permanent 1px rim highlight and click-activated cursor-tracking caustics.
@interface BudsRootView : NSView
@property (nonatomic, assign) BOOL dragUnlocked;
@property (nonatomic, assign) BOOL dragInProgress;
@property (nonatomic, assign) BOOL causticsActive;
@property (nonatomic, assign) NSInteger pressToken;
@property (nonatomic, strong) NSTrackingArea *trackingArea;
@property (nonatomic, strong) CAGradientLayer *causticsLayer;
@property (nonatomic, strong) CAGradientLayer *rimLayer;
- (void)activateCausticsAtWindowPoint:(NSPoint)winPt;
@end

@implementation BudsRootView

- (void)updateTrackingAreas
{
    [super updateTrackingAreas];
    if (_trackingArea != nil) {
        [self removeTrackingArea:_trackingArea];
    }
    _trackingArea = [[NSTrackingArea alloc] initWithRect:self.bounds
                                                 options:NSTrackingMouseMoved |
                                                         NSTrackingMouseEnteredAndExited |
                                                         NSTrackingActiveAlways |
                                                         NSTrackingInVisibleRect
                                                   owner:self
                                                userInfo:nil];
    [self addTrackingArea:_trackingArea];
}

- (void)updateLightAtPoint:(NSPoint)pt
{
    CGFloat w = MAX(1.0, self.bounds.size.width);
    CGFloat h = MAX(1.0, self.bounds.size.height);
    CGFloat nx = MAX(0.0, MIN(1.0, pt.x / w));
    CGFloat ny = MAX(0.0, MIN(1.0, pt.y / h));

    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    if (_causticsLayer != nil && _causticsActive) {
        _causticsLayer.position = CGPointMake(pt.x, pt.y);
    }
    if (_rimLayer != nil) {
        _rimLayer.startPoint = CGPointMake(nx, ny);
        CGFloat dx = nx < 0.5 ? nx + 0.55 : nx - 0.55;
        CGFloat dy = ny < 0.5 ? ny + 0.55 : ny - 0.55;
        _rimLayer.endPoint = CGPointMake(dx, dy);
    }
    [CATransaction commit];
}

- (void)activateCausticsAtWindowPoint:(NSPoint)winPt
{
    NSPoint pt = [self convertPoint:winPt fromView:nil];
    _causticsActive = YES;
    [self updateLightAtPoint:pt];
    [CATransaction begin];
    [CATransaction setAnimationDuration:0.20];
    _causticsLayer.opacity = 1.0f;
    [CATransaction commit];
}

- (void)mouseEntered:(NSEvent *)event
{
    // Do not react to hover; light tracking activates only after click
}

- (void)mouseMoved:(NSEvent *)event
{
    if (!_causticsActive) {
        return;
    }
    NSPoint pt = [self convertPoint:event.locationInWindow fromView:nil];
    [self updateLightAtPoint:pt];
}

- (void)mouseExited:(NSEvent *)event
{
    if (!_causticsActive) {
        return;
    }
    _causticsActive = NO;
    [CATransaction begin];
    [CATransaction setAnimationDuration:0.25];
    _causticsLayer.opacity = 0.0f;
    _rimLayer.startPoint = CGPointMake(0.18, 0.92);
    _rimLayer.endPoint = CGPointMake(0.82, 0.08);
    [CATransaction commit];
}

- (void)mouseDown:(NSEvent *)event
{
    _dragUnlocked = NO;
    _dragInProgress = NO;
    _pressToken++;

    [self activateCausticsAtWindowPoint:event.locationInWindow];
    NSPoint pt = [self convertPoint:event.locationInWindow fromView:nil];

    // Only the top header region (y >= 112) can initiate dragging
    if (pt.y < 112.0) {
        return;
    }

    NSInteger token = _pressToken;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.25 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
        if (self->_pressToken != token) {
            return;
        }
        self->_dragUnlocked = YES;
        [[NSCursor openHandCursor] set];
        self.alphaValue = 0.94;
    });
}

- (void)mouseDragged:(NSEvent *)event
{
    if (!_dragUnlocked || _dragInProgress) {
        return;
    }
    _dragInProgress = YES;
    [self.window performWindowDragWithEvent:event];

    // performWindowDragWithEvent: blocks until mouse-up
    _dragInProgress = NO;
    _dragUnlocked = NO;
    _pressToken++;
    [[NSCursor arrowCursor] set];
    self.alphaValue = 1.0;

    NSPoint origin = self.window.frame.origin;
    NSUserDefaults *defaults = [NSUserDefaults standardUserDefaults];
    [defaults setDouble:origin.x forKey:kPrefOriginX];
    [defaults setDouble:origin.y forKey:kPrefOriginY];
}

- (void)mouseUp:(NSEvent *)event
{
    _pressToken++;
    if (_dragUnlocked) {
        _dragUnlocked = NO;
        [[NSCursor arrowCursor] set];
        self.alphaValue = 1.0;
    }
}

@end

@interface BudsBatteryInlineItem : NSView
@property (nonatomic, strong) NSView *dotView;
@property (nonatomic, strong) NSTextField *textLabel;
@property (nonatomic, copy) NSString *prefix;
@end

@implementation BudsBatteryInlineItem

- (instancetype)initWithFrame:(NSRect)frameRect prefix:(NSString *)prefix
{
    self = [super initWithFrame:frameRect];
    if (self) {
        _prefix = [prefix copy];

        _dotView = [[NSView alloc] initWithFrame:NSMakeRect(0, 6, 6, 6)];
        _dotView.wantsLayer = YES;
        _dotView.layer.cornerRadius = 3.0;
        _dotView.layer.backgroundColor = [NSColor tertiaryLabelColor].CGColor;
        [self addSubview:_dotView];

        _textLabel = [NSTextField labelWithString:[NSString stringWithFormat:@"%@ --", prefix]];
        _textLabel.frame = NSMakeRect(10, 0, frameRect.size.width - 10, 18);
        _textLabel.font = [NSFont monospacedDigitSystemFontOfSize:12 weight:NSFontWeightMedium];
        _textLabel.textColor = [NSColor secondaryLabelColor];
        [self addSubview:_textLabel];
    }
    return self;
}

- (void)updatePercent:(int32_t)percent dimmed:(BOOL)dimmed
{
    if (percent <= 0 || percent > 100) {
        _textLabel.stringValue = [NSString stringWithFormat:@"%@ --", _prefix];
        _textLabel.textColor = [NSColor tertiaryLabelColor];
        _dotView.layer.backgroundColor = [NSColor tertiaryLabelColor].CGColor;
        self.alphaValue = 0.72;
        return;
    }

    _textLabel.stringValue = [NSString stringWithFormat:@"%@ %d%%", _prefix, percent];
    _textLabel.textColor = dimmed ? [NSColor tertiaryLabelColor] : [NSColor secondaryLabelColor];
    self.alphaValue = dimmed ? 0.75 : 1.0;

    NSColor *dotColor;
    if (percent <= 15) {
        dotColor = [NSColor systemRedColor];
    } else if (percent <= 30) {
        dotColor = [NSColor systemOrangeColor];
    } else {
        dotColor = [NSColor systemGreenColor];
    }
    _dotView.layer.backgroundColor = (dimmed ? [dotColor colorWithAlphaComponent:0.55] : dotColor).CGColor;
}

@end

@interface BudsNoiseSegment : NSView
@property (nonatomic, assign) int32_t actionCode;
@property (nonatomic, assign) BOOL selectedState;
@property (nonatomic, assign) BOOL enabledState;
@property (nonatomic, strong) NSImageView *iconView;
@property (nonatomic, strong) NSTextField *labelView;
@property (nonatomic, strong) NSColor *accentColor;
@property (nonatomic, copy) void (^onTap)(int32_t actionCode);
@end

@implementation BudsNoiseSegment

- (instancetype)initWithFrame:(NSRect)frameRect
                        title:(NSString *)title
                   symbolName:(NSString *)symbolName
                  accentColor:(NSColor *)accentColor
                   actionCode:(int32_t)actionCode
{
    self = [super initWithFrame:frameRect];
    if (self) {
        _actionCode = actionCode;
        _enabledState = YES;
        _accentColor = accentColor;
        self.wantsLayer = YES;
        self.layer.cornerRadius = 14.0;
        self.layer.masksToBounds = YES;

        NSImage *img = [NSImage imageWithSystemSymbolName:symbolName accessibilityDescription:title];
        img = [img imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:14 weight:NSFontWeightSemibold]];

        _iconView = [[NSImageView alloc] initWithFrame:NSMakeRect((frameRect.size.width - 20) / 2.0, 23, 20, 18)];
        _iconView.image = img;
        _iconView.imageScaling = NSImageScaleProportionallyDown;
        [self addSubview:_iconView];

        _labelView = [NSTextField labelWithString:title];
        _labelView.frame = NSMakeRect(2, 6, frameRect.size.width - 4, 14);
        _labelView.alignment = NSTextAlignmentCenter;
        _labelView.font = [NSFont systemFontOfSize:11 weight:NSFontWeightMedium];
        [self addSubview:_labelView];

        [self applyVisualState];
    }
    return self;
}

- (void)applyVisualState
{
    self.alphaValue = _enabledState ? 1.0 : 0.38;
    BOOL active = _selectedState && _enabledState;

    NSColor *fgColor = active ? [NSColor whiteColor] : [NSColor secondaryLabelColor];
    _labelView.textColor = fgColor;
    _iconView.contentTintColor = fgColor;
}

- (void)setSelectedState:(BOOL)selectedState
{
    _selectedState = selectedState;
    [self applyVisualState];
}

- (void)setEnabledState:(BOOL)enabledState
{
    _enabledState = enabledState;
    [self applyVisualState];
}

- (void)mouseDown:(NSEvent *)event
{
    if ([self.window.contentView isKindOfClass:[BudsRootView class]]) {
        BudsRootView *rv = (BudsRootView *)self.window.contentView;
        [rv activateCausticsAtWindowPoint:event.locationInWindow];
    }
    if (!_enabledState) {
        return;
    }
    self.alphaValue = 0.70;
}

- (void)mouseUp:(NSEvent *)event
{
    if (!_enabledState) {
        return;
    }
    self.alphaValue = 1.0;
    NSPoint pt = [self convertPoint:event.locationInWindow fromView:nil];
    if (NSPointInRect(pt, self.bounds) && _onTap != nil) {
        _onTap(_actionCode);
    }
}

@end

@interface BudsWidgetTarget : NSObject
@property (nonatomic, copy) void (^onConnect)(void);
@property (nonatomic, copy) void (^onClose)(void);
@property (nonatomic, copy) void (^onPostureToggle)(void);
@property (nonatomic, copy) void (^onPostureCalibrate)(void);
- (void)handleConnect:(id)sender;
- (void)handleClose:(id)sender;
- (void)handlePostureToggle:(id)sender;
- (void)handlePostureCalibrate:(id)sender;
@end

@implementation BudsWidgetTarget
- (void)handleConnect:(id)sender
{
    if (_onConnect) _onConnect();
}
- (void)handleClose:(id)sender
{
    if (_onClose) _onClose();
}
- (void)handlePostureToggle:(id)sender
{
    if (_onPostureToggle) _onPostureToggle();
}
- (void)handlePostureCalibrate:(id)sender
{
    if (_onPostureCalibrate) _onPostureCalibrate();
}
@end

static BudsWidgetPanel *gPanel = nil;
static BudsWidgetActionCallbackFn gCallback = NULL;
static BudsWidgetTarget *gTarget = nil;

static NSView *gRootContent = nil;
static NSView *gMainContentGroup = nil;
static NSView *gStatusDot = nil;
static NSImageView *gHeaderIcon = nil;
static NSTextField *gDeviceLabel = nil;
static NSButton *gConnectButton = nil;
static NSButton *gPostureToggleBtn = nil;
static NSButton *gPostureCalibrateBtn = nil;

static BudsBatteryInlineItem *gBattL = nil;
static BudsBatteryInlineItem *gBattR = nil;
static BudsBatteryInlineItem *gBattCase = nil;

static BudsNoiseSegment *gBtnOff = nil;
static BudsNoiseSegment *gBtnAdaptive = nil;
static BudsNoiseSegment *gBtnAnc = nil;
static BudsNoiseSegment *gBtnAmbient = nil;

// Connect animation overlay
static NSView *gHeroOverlay = nil;
static NSImageView *gHeroIcon = nil;
static NSTextField *gHeroLabel = nil;
static CAShapeLayer *gHeroRipple = nil;
static NSInteger gAnimToken = 0;
static NSView *gActivePill = nil;

static void DispatchAction(int32_t action)
{
    BudsWidgetActionCallbackFn cb = gCallback;
    if (cb != NULL) {
        cb(action);
    }
}

static void SavePanelOrigin(NSWindow *window)
{
    NSPoint origin = window.frame.origin;
    NSUserDefaults *defaults = [NSUserDefaults standardUserDefaults];
    [defaults setDouble:origin.x forKey:kPrefOriginX];
    [defaults setDouble:origin.y forKey:kPrefOriginY];
}

static NSPoint DefaultPanelOrigin(void)
{
    NSScreen *screen = [NSScreen mainScreen] ?: [[NSScreen screens] firstObject];
    if (!screen) {
        return NSMakePoint(100, 100);
    }
    NSRect visible = screen.visibleFrame;
    CGFloat x = NSMaxX(visible) - kWidgetWidth - 24.0;
    CGFloat y = NSMaxY(visible) - kWidgetHeight - 24.0;
    return NSMakePoint(x, y);
}

static void EnsurePanelCreated(void)
{
    if (gPanel != nil) {
        return;
    }

    NSUserDefaults *defaults = [NSUserDefaults standardUserDefaults];
    NSPoint origin;
    if ([defaults objectForKey:kPrefOriginX] != nil && [defaults objectForKey:kPrefOriginY] != nil) {
        origin = NSMakePoint([defaults doubleForKey:kPrefOriginX], [defaults doubleForKey:kPrefOriginY]);
        BOOL onAnyScreen = NO;
        NSRect candidate = NSMakeRect(origin.x, origin.y, kWidgetWidth, kWidgetHeight);
        for (NSScreen *screen in [NSScreen screens]) {
            if (NSIntersectsRect(screen.visibleFrame, candidate)) {
                onAnyScreen = YES;
                break;
            }
        }
        if (!onAnyScreen) {
            origin = DefaultPanelOrigin();
        }
    } else {
        origin = DefaultPanelOrigin();
    }

    NSRect frame = NSMakeRect(origin.x, origin.y, kWidgetWidth, kWidgetHeight);
    gPanel = [[BudsWidgetPanel alloc] initWithContentRect:frame
                                                styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel
                                                  backing:NSBackingStoreBuffered
                                                    defer:NO];
    gPanel.opaque = NO;
    gPanel.backgroundColor = [NSColor clearColor];
    gPanel.hasShadow = NO;
    gPanel.level = -2147483602;
    gPanel.hidesOnDeactivate = NO;
    gPanel.movableByWindowBackground = NO;
    gPanel.releasedWhenClosed = NO;
    gPanel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces |
                                NSWindowCollectionBehaviorStationary |
                                NSWindowCollectionBehaviorIgnoresCycle;

    [[NSNotificationCenter defaultCenter] addObserverForName:NSWindowDidMoveNotification
                                                      object:gPanel
                                                       queue:[NSOperationQueue mainQueue]
                                                  usingBlock:^(NSNotification *note) {
        SavePanelOrigin(gPanel);
    }];

    NSRect bounds = NSMakeRect(0, 0, kWidgetWidth, kWidgetHeight);
    gRootContent = [[BudsRootView alloc] initWithFrame:bounds];
    gRootContent.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    gRootContent.wantsLayer = YES;
    gRootContent.layer.cornerRadius = kCornerRadius;
    gRootContent.layer.masksToBounds = YES;

    if (@available(macOS 26.0, *)) {
        NSGlassEffectView *outerGlass = [[NSGlassEffectView alloc] initWithFrame:bounds];
        outerGlass.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        outerGlass.style = NSGlassEffectViewStyleClear;
        outerGlass.cornerRadius = kCornerRadius;
        outerGlass.wantsLayer = YES;
        outerGlass.layer.shadowOpacity = 0.0f;
        [gRootContent addSubview:outerGlass positioned:NSWindowBelow relativeTo:nil];
    } else {
        NSVisualEffectView *fx = [[NSVisualEffectView alloc] initWithFrame:bounds];
        fx.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        fx.blendingMode = NSVisualEffectBlendingModeBehindWindow;
        fx.material = NSVisualEffectMaterialHUDWindow;
        fx.state = NSVisualEffectStateActive;
        fx.wantsLayer = YES;
        fx.layer.cornerRadius = kCornerRadius;
        fx.layer.masksToBounds = YES;
        [gRootContent addSubview:fx positioned:NSWindowBelow relativeTo:nil];
    }

    gMainContentGroup = [[NSView alloc] initWithFrame:bounds];
    gMainContentGroup.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    gMainContentGroup.wantsLayer = YES;
    [gRootContent addSubview:gMainContentGroup];

    gTarget = [[BudsWidgetTarget alloc] init];
    gTarget.onConnect = ^{ DispatchAction(4); };
    gTarget.onClose = ^{ DispatchAction(5); };
    gTarget.onPostureToggle = ^{ DispatchAction(6); };
    gTarget.onPostureCalibrate = ^{
        DispatchAction(7);
        if (gPostureCalibrateBtn != nil) {
            gPostureCalibrateBtn.contentTintColor = [NSColor systemGreenColor];
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.45 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
                gPostureCalibrateBtn.contentTintColor = [NSColor secondaryLabelColor];
            });
        }
    };

    // Header Row (y = 124..146)
    gStatusDot = [[NSView alloc] initWithFrame:NSMakeRect(18, 133, 7, 7)];
    gStatusDot.wantsLayer = YES;
    gStatusDot.layer.cornerRadius = 3.5;
    gStatusDot.layer.backgroundColor = [NSColor tertiaryLabelColor].CGColor;
    [gMainContentGroup addSubview:gStatusDot];

    NSImage *hpImg = [NSImage imageWithSystemSymbolName:@"earbuds" accessibilityDescription:@"Earbuds"];
    if (!hpImg) {
        hpImg = [NSImage imageWithSystemSymbolName:@"headphones" accessibilityDescription:@"Earbuds"];
    }
    gHeaderIcon = [[NSImageView alloc] initWithFrame:NSMakeRect(30, 128, 16, 16)];
    gHeaderIcon.image = [hpImg imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:12 weight:NSFontWeightSemibold]];
    gHeaderIcon.contentTintColor = [NSColor labelColor];
    [gMainContentGroup addSubview:gHeaderIcon];

    gDeviceLabel = [NSTextField labelWithString:@"Galaxy Buds"];
    gDeviceLabel.frame = NSMakeRect(50, 127, 210, 18);
    gDeviceLabel.font = [NSFont systemFontOfSize:13 weight:NSFontWeightSemibold];
    gDeviceLabel.textColor = [NSColor labelColor];
    gDeviceLabel.lineBreakMode = NSLineBreakByTruncatingTail;
    [gMainContentGroup addSubview:gDeviceLabel];

    gConnectButton = [NSButton buttonWithTitle:@"Connect" target:gTarget action:@selector(handleConnect:)];
    gConnectButton.frame = NSMakeRect(276, 125, 52, 22);
    gConnectButton.bezelStyle = NSBezelStyleAccessoryBarAction;
    gConnectButton.controlSize = NSControlSizeSmall;
    gConnectButton.font = [NSFont systemFontOfSize:11 weight:NSFontWeightMedium];
    gConnectButton.hidden = YES;
    [gMainContentGroup addSubview:gConnectButton];

    // Posture toggle & calibrate mini icon buttons (top-right when connected)
    NSImage *postureImg = [NSImage imageWithSystemSymbolName:@"figure.stand" accessibilityDescription:@"Posture"];
    if (!postureImg) {
        postureImg = [NSImage imageWithSystemSymbolName:@"accessibility" accessibilityDescription:@"Posture"];
    }
    gPostureToggleBtn = [[NSButton alloc] initWithFrame:NSMakeRect(274, 124, 24, 24)];
    gPostureToggleBtn.bordered = NO;
    gPostureToggleBtn.wantsLayer = YES;
    gPostureToggleBtn.layer.cornerRadius = 12.0;
    gPostureToggleBtn.layer.backgroundColor = [[NSColor labelColor] colorWithAlphaComponent:0.08].CGColor;
    gPostureToggleBtn.image = [postureImg imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:11.5 weight:NSFontWeightSemibold]];
    gPostureToggleBtn.contentTintColor = [NSColor secondaryLabelColor];
    gPostureToggleBtn.toolTip = @"坐姿检测开关";
    gPostureToggleBtn.target = gTarget;
    gPostureToggleBtn.action = @selector(handlePostureToggle:);
    gPostureToggleBtn.hidden = YES;
    [gMainContentGroup addSubview:gPostureToggleBtn];

    NSImage *calImg = [NSImage imageWithSystemSymbolName:@"scope" accessibilityDescription:@"Calibrate"];
    if (!calImg) {
        calImg = [NSImage imageWithSystemSymbolName:@"arrow.counterclockwise" accessibilityDescription:@"Calibrate"];
    }
    gPostureCalibrateBtn = [[NSButton alloc] initWithFrame:NSMakeRect(304, 124, 24, 24)];
    gPostureCalibrateBtn.bordered = NO;
    gPostureCalibrateBtn.wantsLayer = YES;
    gPostureCalibrateBtn.layer.cornerRadius = 12.0;
    gPostureCalibrateBtn.layer.backgroundColor = [[NSColor labelColor] colorWithAlphaComponent:0.08].CGColor;
    gPostureCalibrateBtn.image = [calImg imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:11.5 weight:NSFontWeightSemibold]];
    gPostureCalibrateBtn.contentTintColor = [NSColor secondaryLabelColor];
    gPostureCalibrateBtn.toolTip = @"重置/校准坐姿基准";
    gPostureCalibrateBtn.target = gTarget;
    gPostureCalibrateBtn.action = @selector(handlePostureCalibrate:);
    gPostureCalibrateBtn.hidden = YES;
    [gMainContentGroup addSubview:gPostureCalibrateBtn];

    // Weakened Compact Battery Line (y = 88..106), aligned to header (x=18) and capsule (x=14..330)
    gBattL = [[BudsBatteryInlineItem alloc] initWithFrame:NSMakeRect(18, 88, 88, 18) prefix:@"L"];
    gBattR = [[BudsBatteryInlineItem alloc] initWithFrame:NSMakeRect(136, 88, 88, 18) prefix:@"R"];
    gBattCase = [[BudsBatteryInlineItem alloc] initWithFrame:NSMakeRect(250, 88, 80, 18) prefix:@"Case"];
    [gMainContentGroup addSubview:gBattL];
    [gMainContentGroup addSubview:gBattR];
    [gMainContentGroup addSubview:gBattCase];

    // 4-Mode Noise Control Capsule (x = 14, y = 16, w = 316, h = 58)
    NSView *capsuleTrack = [[NSView alloc] initWithFrame:NSMakeRect(14, 16, 316, 58)];
    capsuleTrack.wantsLayer = YES;
    capsuleTrack.layer.cornerRadius = 18.0;
    capsuleTrack.layer.masksToBounds = YES;
    capsuleTrack.layer.backgroundColor = [[NSColor labelColor] colorWithAlphaComponent:0.08].CGColor;
    [gMainContentGroup addSubview:capsuleTrack];

    if (@available(macOS 26.0, *)) {
        NSGlassEffectView *pill = [[NSGlassEffectView alloc] initWithFrame:NSMakeRect(4, 4, 75, 50)];
        pill.style = NSGlassEffectViewStyleClear;
        pill.cornerRadius = 14.0;
        pill.tintColor = [[NSColor systemBlueColor] colorWithAlphaComponent:0.52];
        pill.wantsLayer = YES;
        pill.layer.shadowOpacity = 0.0f;
        pill.alphaValue = 0.0;
        [capsuleTrack addSubview:pill];
        gActivePill = pill;
    } else {
        NSView *pill = [[NSView alloc] initWithFrame:NSMakeRect(4, 4, 75, 50)];
        pill.wantsLayer = YES;
        pill.layer.cornerRadius = 14.0;
        pill.layer.backgroundColor = [[NSColor systemBlueColor] colorWithAlphaComponent:0.62].CGColor;
        pill.alphaValue = 0.0;
        [capsuleTrack addSubview:pill];
        gActivePill = pill;
    }

    void (^tapBlock)(int32_t) = ^(int32_t actionCode) {
        DispatchAction(actionCode);
    };

    // 4 segments: 4 + 75 + 3 + 75 + 3 + 75 + 3 + 74 + 4 = 316
    CGFloat segY = 4.0;
    CGFloat segH = 50.0;
    gBtnOff = [[BudsNoiseSegment alloc] initWithFrame:NSMakeRect(4, segY, 75, segH)
                                                title:@"关闭"
                                           symbolName:@"xmark.circle"
                                          accentColor:[NSColor systemGrayColor]
                                           actionCode:0];
    gBtnOff.onTap = tapBlock;

    gBtnAdaptive = [[BudsNoiseSegment alloc] initWithFrame:NSMakeRect(82, segY, 75, segH)
                                                     title:@"自适应"
                                                symbolName:@"sparkles"
                                               accentColor:[NSColor systemTealColor]
                                                actionCode:3];
    gBtnAdaptive.onTap = tapBlock;

    gBtnAnc = [[BudsNoiseSegment alloc] initWithFrame:NSMakeRect(160, segY, 75, segH)
                                                title:@"降噪"
                                           symbolName:@"headphones"
                                          accentColor:[NSColor systemBlueColor]
                                           actionCode:2];
    gBtnAnc.onTap = tapBlock;

    gBtnAmbient = [[BudsNoiseSegment alloc] initWithFrame:NSMakeRect(238, segY, 74, segH)
                                                    title:@"环境音"
                                               symbolName:@"waveform"
                                              accentColor:[NSColor systemIndigoColor]
                                               actionCode:1];
    gBtnAmbient.onTap = tapBlock;

    [capsuleTrack addSubview:gBtnOff];
    [capsuleTrack addSubview:gBtnAdaptive];
    [capsuleTrack addSubview:gBtnAnc];
    [capsuleTrack addSubview:gBtnAmbient];

    // Connect Hero Animation Overlay (hidden by default)
    gHeroOverlay = [[NSView alloc] initWithFrame:bounds];
    gHeroOverlay.wantsLayer = YES;
    gHeroOverlay.hidden = YES;
    [gRootContent addSubview:gHeroOverlay];

    gHeroRipple = [CAShapeLayer layer];
    CGFloat rippleSize = 68.0;
    CGPathRef ripplePath = CGPathCreateWithEllipseInRect(CGRectMake(0, 0, rippleSize, rippleSize), NULL);
    gHeroRipple.path = ripplePath;
    CGPathRelease(ripplePath);
    gHeroRipple.bounds = CGRectMake(0, 0, rippleSize, rippleSize);
    gHeroRipple.position = CGPointMake(kWidgetWidth / 2.0, 92.0);
    gHeroRipple.fillColor = [NSColor clearColor].CGColor;
    gHeroRipple.strokeColor = [[NSColor systemGreenColor] colorWithAlphaComponent:0.55].CGColor;
    gHeroRipple.lineWidth = 2.0;
    [gHeroOverlay.layer addSublayer:gHeroRipple];

    gHeroIcon = [[NSImageView alloc] initWithFrame:NSMakeRect((kWidgetWidth - 46) / 2.0, 69, 46, 46)];
    gHeroIcon.wantsLayer = YES;
    gHeroIcon.image = [hpImg imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:36 weight:NSFontWeightMedium]];
    gHeroIcon.contentTintColor = [NSColor labelColor];
    [gHeroOverlay addSubview:gHeroIcon];

    gHeroLabel = [NSTextField labelWithString:@"已连接"];
    gHeroLabel.frame = NSMakeRect(24, 30, kWidgetWidth - 48, 22);
    gHeroLabel.alignment = NSTextAlignmentCenter;
    gHeroLabel.font = [NSFont systemFontOfSize:13 weight:NSFontWeightSemibold];
    gHeroLabel.textColor = [NSColor labelColor];
    [gHeroOverlay addSubview:gHeroLabel];

    // Cursor-tracking radial caustics glow (160pt radius, fades in on hover)
    CAGradientLayer *caustics = [CAGradientLayer layer];
    caustics.type = kCAGradientLayerRadial;
    caustics.bounds = CGRectMake(0, 0, 320.0, 320.0);
    caustics.position = CGPointMake(bounds.size.width * 0.25, bounds.size.height * 0.85);
    caustics.startPoint = CGPointMake(0.5, 0.5);
    caustics.endPoint = CGPointMake(1.0, 1.0);
    caustics.colors = @[
        (id)[[NSColor whiteColor] colorWithAlphaComponent:0.10].CGColor,
        (id)[NSColor colorWithRed:0.61 green:0.86 blue:1.0 alpha:0.04].CGColor,
        (id)[NSColor clearColor].CGColor
    ];
    caustics.locations = @[@0.0, @0.45, @1.0];
    caustics.opacity = 0.0f;
    caustics.zPosition = 997.0;
    [gRootContent.layer addSublayer:caustics];

    // Top-layer 1px specular glass rim highlight (always visible, tracks cursor)
    CAGradientLayer *rimGrad = [CAGradientLayer layer];
    rimGrad.frame = bounds;
    rimGrad.zPosition = 999.0;
    rimGrad.opacity = 0.88f;
    rimGrad.colors = @[
        (id)[[NSColor whiteColor] colorWithAlphaComponent:0.78].CGColor,
        (id)[NSColor colorWithRed:0.61 green:0.88 blue:1.0 alpha:0.34].CGColor,
        (id)[[NSColor whiteColor] colorWithAlphaComponent:0.06].CGColor
    ];
    rimGrad.locations = @[@0.0, @0.42, @1.0];
    rimGrad.startPoint = CGPointMake(0.18, 0.92);
    rimGrad.endPoint = CGPointMake(0.82, 0.08);

    CAShapeLayer *rimMask = [CAShapeLayer layer];
    rimMask.frame = bounds;
    CGPathRef rimPath = CGPathCreateWithRoundedRect(CGRectInset(bounds, 0.5, 0.5), kCornerRadius - 0.5, kCornerRadius - 0.5, NULL);
    rimMask.path = rimPath;
    CGPathRelease(rimPath);
    rimMask.fillColor = [NSColor clearColor].CGColor;
    rimMask.strokeColor = [NSColor whiteColor].CGColor;
    rimMask.lineWidth = 1.0;
    rimGrad.mask = rimMask;
    [gRootContent.layer addSublayer:rimGrad];

    if ([gRootContent isKindOfClass:[BudsRootView class]]) {
        BudsRootView *rv = (BudsRootView *)gRootContent;
        rv.causticsLayer = caustics;
        rv.rimLayer = rimGrad;
    }

    gPanel.contentView = gRootContent;
}

// MARK: - Public C API

extern "C" {

void BudsWidgetSetActionCallback(BudsWidgetActionCallbackFn callback)
{
    gCallback = callback;
}

void BudsWidgetShow(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        EnsurePanelCreated();
        [gPanel orderFrontRegardless];
    });
}

void BudsWidgetHide(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        if (gPanel != nil) {
            [gPanel orderOut:nil];
        }
    });
}

void BudsWidgetPlayConnectAnimation(const char *deviceName, bool isChinese)
{
    NSString *nameStr = deviceName != NULL ? [NSString stringWithUTF8String:deviceName] : @"Galaxy Buds";
    dispatch_async(dispatch_get_main_queue(), ^{
        EnsurePanelCreated();
        if (!gPanel.isVisible) {
            return;
        }

        gAnimToken++;
        NSInteger token = gAnimToken;

        // Reset hero state
        [gHeroIcon.layer removeAllAnimations];
        [gHeroRipple removeAllAnimations];
        gHeroIcon.frame = NSMakeRect((kWidgetWidth - 46) / 2.0, 69, 46, 46);
        gHeroIcon.alphaValue = 1.0;
        NSString *prefix = isChinese ? @"已连接" : @"Connected";
        gHeroLabel.stringValue = [NSString stringWithFormat:@"%@ · %@", prefix, nameStr.length > 0 ? nameStr : @"Galaxy Buds"];
        gHeroLabel.alphaValue = 1.0;
        gHeroRipple.opacity = 1.0;
        gHeroOverlay.hidden = NO;

        gHeaderIcon.hidden = YES;
        [NSAnimationContext runAnimationGroup:^(NSAnimationContext *ctx) {
            ctx.duration = 0.16;
            gMainContentGroup.animator.alphaValue = 0.0;
        }];

        // Ripple expand + fade
        CABasicAnimation *rippleScale = [CABasicAnimation animationWithKeyPath:@"transform.scale"];
        rippleScale.fromValue = @0.65;
        rippleScale.toValue = @1.45;
        rippleScale.duration = 1.0;
        rippleScale.timingFunction = [CAMediaTimingFunction functionWithName:kCAMediaTimingFunctionEaseOut];

        CABasicAnimation *rippleAlpha = [CABasicAnimation animationWithKeyPath:@"opacity"];
        rippleAlpha.fromValue = @0.85;
        rippleAlpha.toValue = @0.0;
        rippleAlpha.duration = 1.0;
        rippleAlpha.fillMode = kCAFillModeForwards;
        rippleAlpha.removedOnCompletion = NO;

        [gHeroRipple addAnimation:rippleScale forKey:@"rippleScale"];
        [gHeroRipple addAnimation:rippleAlpha forKey:@"rippleAlpha"];

        // Icon pop-in
        CABasicAnimation *pop = [CABasicAnimation animationWithKeyPath:@"transform.scale"];
        pop.fromValue = @0.55;
        pop.toValue = @1.0;
        pop.duration = 0.28;
        pop.timingFunction = [CAMediaTimingFunction functionWithName:kCAMediaTimingFunctionEaseOut];
        [gHeroIcon.layer addAnimation:pop forKey:@"pop"];

        // After 1.15s, shrink and fly into top-left header icon frame (30, 128, 16, 16)
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(1.15 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
            if (token != gAnimToken) {
                return;
            }

            [NSAnimationContext runAnimationGroup:^(NSAnimationContext *ctx) {
                ctx.duration = 0.42;
                ctx.timingFunction = [CAMediaTimingFunction functionWithName:kCAMediaTimingFunctionEaseInEaseOut];
                gHeroIcon.animator.frame = gHeaderIcon.frame;
                gHeroLabel.animator.alphaValue = 0.0;
                gMainContentGroup.animator.alphaValue = 1.0;
            } completionHandler:^{
                if (token != gAnimToken) {
                    return;
                }
                gHeroOverlay.hidden = YES;
                gHeaderIcon.hidden = NO;
            }];
        });
    });
}

void BudsWidgetUpdateState(bool connected,
                           const char *deviceName,
                           int32_t battL,
                           int32_t battR,
                           int32_t battCase,
                           bool caseCached,
                           int32_t noiseMode,
                           bool noiseSupported,
                           bool adaptiveSupported,
                           bool postureEnabled,
                           bool isChinese)
{
    NSString *nameStr = deviceName != NULL ? [NSString stringWithUTF8String:deviceName] : @"Galaxy Buds";
    dispatch_async(dispatch_get_main_queue(), ^{
        EnsurePanelCreated();

        gConnectButton.title = isChinese ? @"连接" : @"Connect";
        gBattCase.prefix = isChinese ? @"盒" : @"Case";
        gBtnOff.labelView.stringValue = isChinese ? @"关闭" : @"Off";
        gBtnAdaptive.labelView.stringValue = isChinese ? @"自适应" : @"Adaptive";
        gBtnAnc.labelView.stringValue = isChinese ? @"降噪" : @"ANC";
        gBtnAmbient.labelView.stringValue = isChinese ? @"环境音" : @"Ambient";
        gPostureToggleBtn.toolTip = isChinese ? @"坐姿检测开关" : @"Posture Monitor";
        gPostureCalibrateBtn.toolTip = isChinese ? @"重置/校准坐姿基准" : @"Calibrate Posture";

        gDeviceLabel.stringValue = nameStr.length > 0 ? nameStr : @"Galaxy Buds";
        gStatusDot.layer.backgroundColor = (connected ? [NSColor systemGreenColor] : [NSColor tertiaryLabelColor]).CGColor;
        gConnectButton.hidden = connected;
        gPostureToggleBtn.hidden = !connected;
        gPostureCalibrateBtn.hidden = !connected;

        if (connected) {
            if (postureEnabled) {
                gPostureToggleBtn.layer.backgroundColor = [[NSColor systemTealColor] colorWithAlphaComponent:0.42].CGColor;
                gPostureToggleBtn.contentTintColor = [NSColor whiteColor];
            } else {
                gPostureToggleBtn.layer.backgroundColor = [[NSColor labelColor] colorWithAlphaComponent:0.08].CGColor;
                gPostureToggleBtn.contentTintColor = [NSColor secondaryLabelColor];
            }
        }

        [gBattL updatePercent:(connected ? battL : 0) dimmed:NO];
        [gBattR updatePercent:(connected ? battR : 0) dimmed:NO];
        [gBattCase updatePercent:(connected ? battCase : 0) dimmed:caseCached];

        BOOL controlsEnabled = connected && noiseSupported;
        gBtnOff.enabledState = controlsEnabled;
        gBtnAnc.enabledState = controlsEnabled;
        gBtnAmbient.enabledState = controlsEnabled;
        gBtnAdaptive.enabledState = controlsEnabled && adaptiveSupported;

        gBtnOff.selectedState = controlsEnabled && (noiseMode == 0);
        gBtnAmbient.selectedState = controlsEnabled && (noiseMode == 1);
        gBtnAnc.selectedState = controlsEnabled && (noiseMode == 2);
        gBtnAdaptive.selectedState = controlsEnabled && adaptiveSupported && (noiseMode == 3);

        BudsNoiseSegment *targetSeg = nil;
        if (controlsEnabled) {
            switch (noiseMode) {
                case 1: targetSeg = gBtnAmbient; break;
                case 2: targetSeg = gBtnAnc; break;
                case 3: targetSeg = adaptiveSupported ? gBtnAdaptive : gBtnOff; break;
                default: targetSeg = gBtnOff; break;
            }
        }

        if (targetSeg == nil) {
            [NSAnimationContext runAnimationGroup:^(NSAnimationContext *ctx) {
                ctx.duration = 0.20;
                gActivePill.animator.alphaValue = 0.0;
            }];
        } else {
            if (@available(macOS 26.0, *)) {
                NSGlassEffectView *glassPill = (NSGlassEffectView *)gActivePill;
                glassPill.tintColor = [targetSeg.accentColor colorWithAlphaComponent:0.52];
            } else {
                gActivePill.layer.backgroundColor = [targetSeg.accentColor colorWithAlphaComponent:0.62].CGColor;
            }

            if (gActivePill.alphaValue < 0.05) {
                gActivePill.frame = targetSeg.frame;
                [NSAnimationContext runAnimationGroup:^(NSAnimationContext *ctx) {
                    ctx.duration = 0.22;
                    gActivePill.animator.alphaValue = 1.0;
                }];
            } else {
                [NSAnimationContext runAnimationGroup:^(NSAnimationContext *ctx) {
                    ctx.duration = 0.26;
                    ctx.timingFunction = [CAMediaTimingFunction functionWithName:kCAMediaTimingFunctionEaseOut];
                    gActivePill.animator.frame = targetSeg.frame;
                    gActivePill.animator.alphaValue = 1.0;
                }];
            }
        }
    });
}

}
