using System;
using PathHide;
using Xunit;

namespace PathHide.Tests;

/// <summary>
/// Nanoid generation: default and explicit lengths, the URL-safe alphabet, and that successive calls
/// do not collide in practice.
/// </summary>
public sealed class NanoIdTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

    [Fact]
    public void New_defaults_to_21_characters()
    {
        Assert.Equal(21, NanoId.New().Length);
    }

    [Fact]
    public void New_honors_an_explicit_length()
    {
        Assert.Equal(10, NanoId.New(10).Length);
        Assert.Equal(1, NanoId.New(1).Length);
    }

    [Fact]
    public void New_uses_only_the_url_safe_alphabet()
    {
        // A long id makes it overwhelmingly likely every alphabet character class is exercised at
        // least once, while the per-character assertion below is what actually pins the alphabet.
        var id = NanoId.New(500);
        Assert.All(id, c => Assert.Contains(c, Alphabet));
    }

    [Fact]
    public void New_calls_differ()
    {
        Assert.NotEqual(NanoId.New(), NanoId.New());
    }

    [Fact]
    public void New_rejects_non_positive_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NanoId.New(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NanoId.New(-1));
    }
}
