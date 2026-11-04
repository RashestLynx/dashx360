using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SharpDX;
using SharpDX.DirectInput;

namespace XboxMetroLauncher.Input;

public sealed class ControllerInputService : IDisposable
{
	private struct XInputState
	{
		public uint PacketNumber;

		public XInputGamepad Gamepad;
	}

	// XInputGetStateEx writes four bytes more than XInputGetState.
	private struct XInputStateEx
	{
		public XInputState State;

		public uint Reserved;
	}

	private struct XInputGamepad
	{
		public ushort Buttons;

		public byte LeftTrigger;

		public byte RightTrigger;

		public short LeftThumbX;

		public short LeftThumbY;

		public short RightThumbX;

		public short RightThumbY;
	}

	private enum InputBackend
	{
		None,
		XInput,
		DirectInput
	}

	private readonly record struct DirectInputButtonMap(int Activate, int Back, int Details, int Search, int PreviousTab, int NextTab, int LeftTrigger, int RightTrigger, int SecondaryBack, int Options, int LeftThumbClick, int Guide)
	{
		public static DirectInputButtonMap PlayStation { get; } = new DirectInputButtonMap(1, 2, 0, 3, 4, 5, 6, 7, 8, 9, 10, 12);

		public static DirectInputButtonMap Switch { get; } = new DirectInputButtonMap(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12);

		public static DirectInputButtonMap Generic { get; } = new DirectInputButtonMap(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12);

		public static DirectInputButtonMap FromProductName(string? productName)
		{
			string text = productName ?? string.Empty;
			if (IsPlayStationProduct(text))
			{
				return PlayStation;
			}
			if (text.Contains("switch", StringComparison.OrdinalIgnoreCase) || text.Contains("joy-con", StringComparison.OrdinalIgnoreCase) || text.Contains("joycon", StringComparison.OrdinalIgnoreCase) || text.Contains("pro controller", StringComparison.OrdinalIgnoreCase))
			{
				return Switch;
			}
			return Generic;
		}

		public static bool IsPlayStationProduct(string? productName)
		{
			string text = productName ?? string.Empty;
			if (!text.Contains("playstation", StringComparison.OrdinalIgnoreCase) && !text.Contains("dualshock", StringComparison.OrdinalIgnoreCase) && !text.Contains("dualsense", StringComparison.OrdinalIgnoreCase) && !text.Contains("wireless controller", StringComparison.OrdinalIgnoreCase))
			{
				return text.Contains("sony", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		public static bool IsKnownGameControllerProduct(string? productName)
		{
			string text = productName ?? string.Empty;
			if (!IsPlayStationProduct(text) && !text.Contains("xbox", StringComparison.OrdinalIgnoreCase) && !text.Contains("controller", StringComparison.OrdinalIgnoreCase) && !text.Contains("gamepad", StringComparison.OrdinalIgnoreCase) && !text.Contains("switch", StringComparison.OrdinalIgnoreCase) && !text.Contains("joy-con", StringComparison.OrdinalIgnoreCase))
			{
				return text.Contains("joycon", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
	}

	private const int ErrorSuccess = 0;

	private const ushort XinputGamepadDpadUp = 1;

	private const ushort XinputGamepadDpadDown = 2;

	private const ushort XinputGamepadDpadLeft = 4;

	private const ushort XinputGamepadDpadRight = 8;

	private const ushort XinputGamepadStart = 16;

	private const ushort XinputGamepadBack = 32;

	private const ushort XinputGamepadLeftThumb = 64;

	private const ushort XinputGamepadGuide = 1024;

	private const ushort XinputGamepadLeftShoulder = 256;

	private const ushort XinputGamepadRightShoulder = 512;

	private const ushort XinputGamepadA = 4096;

	private const ushort XinputGamepadB = 8192;

	private const ushort XinputGamepadX = 16384;

	private const ushort XinputGamepadY = 32768;

	private const short ThumbDeadzone = 16000;

	private const byte TriggerThreshold = 120;

	private const int DirectInputAxisCenter = 32767;

	private const int DirectInputThumbDeadzone = 14000;

	private const int DirectInputPovNeutral = -1;

	private const bool EnableDirectInputBackend = true;

	private readonly Timer _timer;

	private readonly Action<DashboardInputAction> _onAction;

	private readonly Func<bool> _isEnabled;

	private readonly Dictionary<DashboardInputAction, DateTimeOffset> _lastMoveTimes = new Dictionary<DashboardInputAction, DateTimeOffset>();

	private readonly DirectInput _directInput;

	private ushort _previousButtons;

	private XInputState _previousState;

	private JoystickState _previousDirectInputState = new JoystickState();

	private DashboardInputAction? _previousStickAction;

	private DateTimeOffset _lastStickMove = DateTimeOffset.MinValue;

	private static readonly TimeSpan MoveRepeatDelay = TimeSpan.FromMilliseconds(185.0);

	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(95.0);

	private bool _isDispatching;

	private int _isPolling;

	private bool _guideButtonDown;

	private InputBackend _activeBackend;

	private Joystick? _directInputJoystick;

	private DirectInputButtonMap _directInputButtonMap = DirectInputButtonMap.Generic;

	private int _activeXInputUserIndex = -1;

	private DateTimeOffset _directInputRetryAfter = DateTimeOffset.MinValue;

	private bool _isRunning;

	private bool _xInputExAvailable = true;

	public bool IsRunning => _isRunning;

	public ControllerInputService(Action<DashboardInputAction> onAction, Func<bool>? isEnabled = null)
	{
		_onAction = onAction;
		_isEnabled = isEnabled ?? ((Func<bool>)(() => true));
		_directInput = new DirectInput();
		_timer = new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
	}

	public void Start()
	{
		_isRunning = true;
		_timer.Change(TimeSpan.Zero, PollInterval);
	}

	public void Dispose()
	{
		_isRunning = false;
		_timer.Dispose();
		ResetDirectInputJoystick();
		_directInput.Dispose();
	}

	private void OnTick(object? state)
	{
		if (Interlocked.Exchange(ref _isPolling, 1) == 1)
		{
			return;
		}
		try
		{
			PollController();
		}
		catch (Exception exception)
		{
			App.LogException(exception, "ControllerInputService.OnTick");
			ResetInputState();
		}
		finally
		{
			Interlocked.Exchange(ref _isPolling, 0);
		}
	}

	private void PollController()
	{
		XInputState state;
		JoystickState state2;
		if (!_isEnabled())
		{
			ResetInputState();
		}
		else if (TryPollXInput(out state))
		{
			ProcessXInputState(state);
		}
		else if (TryPollDirectInput(out state2))
		{
			ProcessDirectInputState(state2);
		}
		else
		{
			ResetInputState();
		}
	}

	private bool TryPollXInput(out XInputState state)
	{
		try
		{
			// Like the console, every connected pad can drive the dashboard: buttons are combined and
			// the stick pushed furthest wins. (Steam and virtual pads often leave an idle pad in slot 0.)
			bool found = false;
			state = default(XInputState);
			int bestStick = -1;
			for (int i = 0; i < 4; i++)
			{
				if (!TryGetXInputState(i, out var padState))
				{
					continue;
				}
				found = true;
				state.Gamepad.Buttons |= padState.Gamepad.Buttons;
				state.Gamepad.LeftTrigger = Math.Max(state.Gamepad.LeftTrigger, padState.Gamepad.LeftTrigger);
				state.Gamepad.RightTrigger = Math.Max(state.Gamepad.RightTrigger, padState.Gamepad.RightTrigger);
				int stick = Math.Max(Math.Abs((int)padState.Gamepad.LeftThumbX), Math.Abs((int)padState.Gamepad.LeftThumbY));
				if (stick > bestStick)
				{
					bestStick = stick;
					state.Gamepad.LeftThumbX = padState.Gamepad.LeftThumbX;
					state.Gamepad.LeftThumbY = padState.Gamepad.LeftThumbY;
					state.Gamepad.RightThumbX = padState.Gamepad.RightThumbX;
					state.Gamepad.RightThumbY = padState.Gamepad.RightThumbY;
				}
			}
			if (found)
			{
				_activeXInputUserIndex = 0;
				SwitchBackend(InputBackend.XInput);
				return true;
			}
		}
		catch (Exception ex) when (((ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException || ex is SEHException) ? 1 : 0) != 0)
		{
			App.LogException(ex, "ControllerInputService.XInput");
			_timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		}
		state = default(XInputState);
		return false;
	}

	// The public XInputGetState hides the Guide button; the undocumented ordinal 100 reports it.
	private bool TryGetXInputState(int userIndex, out XInputState state)
	{
		if (_xInputExAvailable)
		{
			try
			{
				int result = XInputGetStateEx(userIndex, out var stateEx);
				state = stateEx.State;
				return result == ErrorSuccess;
			}
			catch (EntryPointNotFoundException)
			{
				_xInputExAvailable = false;
			}
		}
		return XInputGetState(userIndex, out state) == ErrorSuccess;
	}

	private bool TryPollDirectInput(out JoystickState state)
	{
		state = new JoystickState();
		if (!EnableDirectInputBackend)
		{
			return false;
		}
		if (DateTimeOffset.UtcNow < _directInputRetryAfter)
		{
			return false;
		}
		try
		{
			Joystick joystick = EnsureDirectInputJoystick();
			if (joystick == null)
			{
				return false;
			}
			try
			{
				joystick.Poll();
				state = joystick.GetCurrentState();
			}
			catch (SharpDXException ex) when (ex.ResultCode == ResultCode.NotAcquired || ex.ResultCode == ResultCode.InputLost)
			{
				joystick.Acquire();
				joystick.Poll();
				state = joystick.GetCurrentState();
			}
			SwitchBackend(InputBackend.DirectInput);
			return true;
		}
		catch (Exception exception)
		{
			App.LogException(exception, "ControllerInputService.DirectInput");
			_directInputRetryAfter = DateTimeOffset.UtcNow.AddSeconds(10.0);
			ResetDirectInputJoystick();
			return false;
		}
	}

	private Joystick? EnsureDirectInputJoystick()
	{
		if (_directInputJoystick != null)
		{
			return _directInputJoystick;
		}
		DeviceInstance deviceInstance = (from device in SafeGetDirectInputDevices(DeviceType.Gamepad).Concat(SafeGetDirectInputDevices(DeviceType.Joystick)).Concat(SafeGetDirectInputDevices(DeviceType.FirstPerson)).Concat(SafeGetDirectInputDevices(DeviceType.Driving))
			group device by device.InstanceGuid into @group
			select @group.First() into device
			orderby DirectInputButtonMap.IsPlayStationProduct(device.ProductName) descending, DirectInputButtonMap.IsKnownGameControllerProduct(device.ProductName) descending
			select device).ToList().FirstOrDefault();
		if (deviceInstance == null)
		{
			return null;
		}
		Joystick joystick = new Joystick(_directInput, deviceInstance.InstanceGuid);
		joystick.Properties.BufferSize = 16;
		joystick.Acquire();
		_directInputJoystick = joystick;
		_directInputButtonMap = DirectInputButtonMap.FromProductName(deviceInstance.ProductName);
		return _directInputJoystick;
	}

	private IEnumerable<DeviceInstance> SafeGetDirectInputDevices(DeviceType deviceType)
	{
		try
		{
			return _directInput.GetDevices(deviceType, DeviceEnumerationFlags.AttachedOnly) ?? new List<DeviceInstance>();
		}
		catch (Exception exception)
		{
			App.LogException(exception, $"ControllerInputService.DirectInput.Enumerate.{deviceType}");
			return Array.Empty<DeviceInstance>();
		}
	}

	private void ProcessXInputState(XInputState state)
	{
		ushort buttons = state.Gamepad.Buttons;
		ushort num = 48;
		if ((buttons & num) == num || (buttons & XinputGamepadGuide) != 0)
		{
			if (!_guideButtonDown)
			{
				DispatchAction(DashboardInputAction.Guide);
			}
			_guideButtonDown = true;
			_previousStickAction = null;
			_previousButtons = buttons;
			_previousState = state;
			return;
		}
		_guideButtonDown = false;
		bool num2 = FireDpadMove(buttons);
		FireOnPress(buttons, 4096, DashboardInputAction.Activate);
		FireOnPress(buttons, 8192, DashboardInputAction.Back);
		FireOnPress(buttons, 16384, DashboardInputAction.Details);
		FireOnPress(buttons, 32768, DashboardInputAction.Search);
		FireOnPress(buttons, 16, DashboardInputAction.Options);
		FireOnPress(buttons, XinputGamepadLeftThumb, DashboardInputAction.LeftThumbClick);
		FireOnPress(buttons, 32, DashboardInputAction.Back);
		FireOnPress(buttons, 256, DashboardInputAction.PreviousTab);
		FireOnPress(buttons, 512, DashboardInputAction.NextTab);
		FireOnTriggerPress(state.Gamepad.LeftTrigger, _previousState.Gamepad.LeftTrigger, DashboardInputAction.LeftTrigger);
		FireOnTriggerPress(state.Gamepad.RightTrigger, _previousState.Gamepad.RightTrigger, DashboardInputAction.RightTrigger);
		DashboardInputAction? stickAction = GetStickAction(state.Gamepad.LeftThumbX, state.Gamepad.LeftThumbY);
		if (!num2 && stickAction.HasValue && ShouldFireStick(stickAction.Value))
		{
			DispatchAction(stickAction.Value);
		}
		_previousStickAction = stickAction;
		_previousButtons = buttons;
		_previousState = state;
	}

	private void ProcessDirectInputState(JoystickState state)
	{
		if (IsDirectInputGuideDown(state))
		{
			if (!_guideButtonDown)
			{
				DispatchAction(DashboardInputAction.Guide);
			}
			_guideButtonDown = true;
			_previousStickAction = null;
			_previousDirectInputState = state;
			return;
		}
		_guideButtonDown = false;
		bool num = FireDirectInputPovMove(state);
		FireOnDirectInputPress(state, _directInputButtonMap.Activate, DashboardInputAction.Activate);
		FireOnDirectInputPress(state, _directInputButtonMap.Back, DashboardInputAction.Back);
		FireOnDirectInputPress(state, _directInputButtonMap.Details, DashboardInputAction.Details);
		FireOnDirectInputPress(state, _directInputButtonMap.Search, DashboardInputAction.Search);
		FireOnDirectInputPress(state, _directInputButtonMap.Options, DashboardInputAction.Options);
		FireOnDirectInputPress(state, _directInputButtonMap.LeftThumbClick, DashboardInputAction.LeftThumbClick);
		FireOnDirectInputPress(state, _directInputButtonMap.SecondaryBack, DashboardInputAction.Back);
		FireOnDirectInputPress(state, _directInputButtonMap.PreviousTab, DashboardInputAction.PreviousTab);
		FireOnDirectInputPress(state, _directInputButtonMap.NextTab, DashboardInputAction.NextTab);
		FireOnDirectInputPress(state, _directInputButtonMap.LeftTrigger, DashboardInputAction.LeftTrigger);
		FireOnDirectInputPress(state, _directInputButtonMap.RightTrigger, DashboardInputAction.RightTrigger);
		DashboardInputAction? directInputStickAction = GetDirectInputStickAction(state.X, state.Y);
		if (!num && directInputStickAction.HasValue && ShouldFireStick(directInputStickAction.Value))
		{
			DispatchAction(directInputStickAction.Value);
		}
		_previousStickAction = directInputStickAction;
		_previousDirectInputState = state;
	}

	private bool FireDpadMove(ushort buttons)
	{
		DashboardInputAction? dpadAction = GetDpadAction(buttons);
		if (!dpadAction.HasValue)
		{
			return false;
		}
		ushort buttonMask = GetButtonMask(dpadAction.Value);
		FireMoveWhileDown(buttons, buttonMask, dpadAction.Value);
		return true;
	}

	private bool FireDirectInputPovMove(JoystickState state)
	{
		DashboardInputAction? directInputPovAction = GetDirectInputPovAction(state);
		if (!directInputPovAction.HasValue)
		{
			return false;
		}
		FireDirectInputMoveWhileDown(state, directInputPovAction.Value);
		return true;
	}

	private void FireOnPress(ushort buttons, ushort mask, DashboardInputAction action)
	{
		if ((buttons & mask) != 0 && (_previousButtons & mask) == 0)
		{
			DispatchAction(action);
		}
	}

	private void FireOnDirectInputPress(JoystickState state, int buttonIndex, DashboardInputAction action)
	{
		if (IsDirectInputButtonDown(state, buttonIndex) && !IsDirectInputButtonDown(_previousDirectInputState, buttonIndex))
		{
			DispatchAction(action);
		}
	}

	private void FireOnTriggerPress(byte value, byte previousValue, DashboardInputAction action)
	{
		if (value >= 120 && previousValue < 120)
		{
			DispatchAction(action);
		}
	}

	private void FireMoveWhileDown(ushort buttons, ushort mask, DashboardInputAction action)
	{
		if ((buttons & mask) != 0)
		{
			if ((_previousButtons & mask) == 0)
			{
				_lastMoveTimes[action] = DateTimeOffset.UtcNow;
			}
			else if (!ShouldRepeat(action))
			{
				return;
			}
			DispatchAction(action);
		}
	}

	private void FireDirectInputMoveWhileDown(JoystickState state, DashboardInputAction action)
	{
		if (GetDirectInputPovAction(_previousDirectInputState) != action)
		{
			_lastMoveTimes[action] = DateTimeOffset.UtcNow;
		}
		else if (!ShouldRepeat(action))
		{
			return;
		}
		DispatchAction(action);
	}

	private bool ShouldRepeat(DashboardInputAction action)
	{
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		if (_lastMoveTimes.TryGetValue(action, out var value) && utcNow - value < MoveRepeatDelay)
		{
			return false;
		}
		_lastMoveTimes[action] = utcNow;
		return true;
	}

	private bool ShouldFireStick(DashboardInputAction action)
	{
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		if (_previousStickAction != action || utcNow - _lastStickMove >= MoveRepeatDelay)
		{
			_lastStickMove = utcNow;
			return true;
		}
		return false;
	}

	private void DispatchAction(DashboardInputAction action)
	{
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val != null && !val.CheckAccess())
		{
			val.BeginInvoke((Delegate)(Action)delegate
			{
				DispatchActionOnUiThread(action);
			}, (DispatcherPriority)10, Array.Empty<object>());
		}
		else
		{
			DispatchActionOnUiThread(action);
		}
	}

	private void DispatchActionOnUiThread(DashboardInputAction action)
	{
		if (_isDispatching)
		{
			return;
		}
		try
		{
			_isDispatching = true;
			_onAction(action);
		}
		catch (Exception exception)
		{
			App.LogException(exception, "ControllerInputService.DispatchAction");
		}
		finally
		{
			_isDispatching = false;
		}
	}

	private static DashboardInputAction? GetDpadAction(ushort buttons)
	{
		bool flag = (buttons & 4) != 0;
		bool flag2 = (buttons & 8) != 0;
		bool flag3 = (buttons & 1) != 0;
		bool flag4 = (buttons & 2) != 0;
		if (flag == flag2 && flag3 == flag4)
		{
			return null;
		}
		if (flag != flag2)
		{
			return (!flag) ? DashboardInputAction.MoveRight : DashboardInputAction.MoveLeft;
		}
		return flag3 ? DashboardInputAction.MoveUp : DashboardInputAction.MoveDown;
	}

	private static DashboardInputAction? GetDirectInputPovAction(JoystickState state)
	{
		int num = state.PointOfViewControllers.FirstOrDefault(-1);
		if (num == -1)
		{
			return null;
		}
		if ((num > 31500 || num < 4500) ? true : false)
		{
			return DashboardInputAction.MoveUp;
		}
		if (num >= 4500 && num < 13500)
		{
			return DashboardInputAction.MoveRight;
		}
		if (num >= 13500 && num < 22500)
		{
			return DashboardInputAction.MoveDown;
		}
		return DashboardInputAction.MoveLeft;
	}

	private static ushort GetButtonMask(DashboardInputAction action)
	{
		return action switch
		{
			DashboardInputAction.MoveLeft => 4, 
			DashboardInputAction.MoveRight => 8, 
			DashboardInputAction.MoveUp => 1, 
			DashboardInputAction.MoveDown => 2, 
			_ => 0, 
		};
	}

	private void ResetInputState()
	{
		_previousButtons = 0;
		_previousState = default(XInputState);
		_previousDirectInputState = new JoystickState();
		_previousStickAction = null;
		_guideButtonDown = false;
		_activeBackend = InputBackend.None;
		_activeXInputUserIndex = -1;
		_lastMoveTimes.Clear();
		ResetDirectInputJoystick();
	}

	private void ResetDirectInputJoystick()
	{
		try
		{
			_directInputJoystick?.Unacquire();
			_directInputJoystick?.Dispose();
		}
		catch
		{
		}
		finally
		{
			_directInputJoystick = null;
		}
	}

	private static DashboardInputAction? GetStickAction(short x, short y)
	{
		int num = Math.Abs((int)x);
		int num2 = Math.Abs((int)y);
		if (num < 16000 && num2 < 16000)
		{
			return null;
		}
		if (num > num2)
		{
			return (x > 0) ? DashboardInputAction.MoveRight : DashboardInputAction.MoveLeft;
		}
		return (y > 0) ? DashboardInputAction.MoveUp : DashboardInputAction.MoveDown;
	}

	private static DashboardInputAction? GetDirectInputStickAction(int x, int y)
	{
		int num = x - 32767;
		int num2 = y - 32767;
		int num3 = Math.Abs(num);
		int num4 = Math.Abs(num2);
		if (num3 < 14000 && num4 < 14000)
		{
			return null;
		}
		if (num3 > num4)
		{
			return (num > 0) ? DashboardInputAction.MoveRight : DashboardInputAction.MoveLeft;
		}
		return (num2 > 0) ? DashboardInputAction.MoveDown : DashboardInputAction.MoveUp;
	}

	private void SwitchBackend(InputBackend backend)
	{
		if (_activeBackend != backend)
		{
			_activeBackend = backend;
			_previousButtons = 0;
			_previousState = default(XInputState);
			_previousDirectInputState = new JoystickState();
			_previousStickAction = null;
			_guideButtonDown = false;
			_lastMoveTimes.Clear();
		}
	}

	private bool IsDirectInputGuideDown(JoystickState state)
	{
		if (!IsDirectInputButtonDown(state, _directInputButtonMap.Guide))
		{
			if (IsDirectInputButtonDown(state, _directInputButtonMap.SecondaryBack))
			{
				return IsDirectInputButtonDown(state, _directInputButtonMap.Options);
			}
			return false;
		}
		return true;
	}

	private static bool IsDirectInputButtonDown(JoystickState state, int index)
	{
		bool[] buttons = state.Buttons;
		if (buttons != null && buttons.Length > 0 && index >= 0 && index < buttons.Length)
		{
			return buttons[index];
		}
		return false;
	}

	[DllImport("xinput1_4.dll")]
	private static extern int XInputGetState(int dwUserIndex, out XInputState pState);

	[DllImport("xinput1_4.dll", EntryPoint = "#100")]
	private static extern int XInputGetStateEx(int dwUserIndex, out XInputStateEx pState);
}
