using System;
using static SDL2.SDL;

namespace Tedd.Commodore.Sdl;

/// <summary>An SDL call failed; the message carries <c>SDL_GetError()</c>.</summary>
public sealed class SdlException : Exception
{
    public SdlException(string operation)
        : base($"{operation} failed: {SDL_GetError()}")
    {
        Operation = operation;
    }

    /// <summary>The SDL function (or logical step) that failed.</summary>
    public string Operation { get; }
}
