// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable enable

using Windows.ApplicationModel.DataTransfer;

namespace Uno.UI.Runtime.X11;

internal enum X11DragReleaseDecision
{
	Wait,
	Drop,
	Leave
}

// A status acknowledges the position actually sent, never a newer move waiting behind it.
internal sealed class X11DragSourceState
{
	private long _revision;
	private long _sentRevision;
	private long _answeredRevision = -1;
	private long _releaseTick;
	private long _dropTick;

	internal bool Outstanding { get; private set; }
	internal bool Released { get; private set; }
	internal bool Dropped { get; private set; }
	internal bool Completed { get; private set; }
	internal bool Accepted { get; private set; }
	internal DataPackageOperation StatusAction { get; private set; }
	internal DataPackageOperation RequestedAction { get; private set; }
	internal DataPackageOperation Result { get; private set; }

	internal void PositionChanged() => _revision++;

	internal bool CanSendPosition(long now)
		=> !Dropped && !Completed && !Outstanding && _sentRevision != _revision
			&& (!Released || now - _releaseTick < 500);

	internal void PositionSent(long now, DataPackageOperation action)
	{
		if (!CanSendPosition(now))
		{
			return;
		}
		_sentRevision = _revision;
		RequestedAction = action;
		Outstanding = true;
	}

	internal void Status(bool accepted, DataPackageOperation action, DataPackageOperation allowed)
	{
		if (!Outstanding || Dropped || Completed)
		{
			return;
		}
		Outstanding = false;
		_answeredRevision = _sentRevision;
		Accepted = accepted && action != DataPackageOperation.None && (action & allowed) == action;
		StatusAction = Accepted ? action : DataPackageOperation.None;
	}

	internal void Release(long now)
	{
		Released = true;
		_releaseTick = now;
	}

	internal X11DragReleaseDecision ReleaseDecision(long now)
	{
		if (now - _releaseTick >= 500)
		{
			return X11DragReleaseDecision.Leave;
		}
		if (Outstanding || _answeredRevision != _revision)
		{
			return X11DragReleaseDecision.Wait;
		}
		return Accepted && StatusAction != DataPackageOperation.None ? X11DragReleaseDecision.Drop : X11DragReleaseDecision.Leave;
	}

	internal void Drop(long now)
	{
		Dropped = true;
		_dropTick = now;
	}

	internal bool Finished(int version, bool accepted, DataPackageOperation? action, DataPackageOperation allowed)
	{
		if (!Dropped || Completed)
		{
			return false;
		}
		var result = version < 5 ? StatusAction
			: !accepted ? DataPackageOperation.None
			: action is null or DataPackageOperation.None ? StatusAction : action.Value;
		return Complete((result & allowed) == result ? result : DataPackageOperation.None);
	}

	internal static bool IsSelectionTimeValid(uint time, uint ownerTime)
		=> time == 0 || unchecked((int)(time - ownerTime)) >= 0;

	internal bool Timeout(long now) => Dropped && now - _dropTick >= 60_000 && Complete(DataPackageOperation.None);

	internal bool Complete(DataPackageOperation result)
	{
		if (Completed)
		{
			return false;
		}
		Completed = true;
		Result = result;
		return true;
	}
}
