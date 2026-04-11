using Dmart.Models.Core;

namespace Dmart.Plugins;

public enum EntryEventKind { Created, Updated, Deleted, StateChanged }

public sealed record EntryEvent(EntryEventKind Kind, Locator Locator, Entry? Entry);
