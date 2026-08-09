using System;
using Delobytes.AspNetCore.Idempotency;
using FluentAssertions;
using Xunit;

namespace Delobytes.AspNetCore.Idempotency.Tests;

public class IdempotencyBodyTypeRegistryTests
{
    private sealed class MyResponseDto
    {
        public string? Name { get; set; }
    }

    private sealed class NotRegisteredDto
    {
    }

    [Fact]
    public void TryResolve_ReturnsTrue_ForDefaultRegisteredSystemTypes()
    {
        IdempotencyBodyTypeRegistry registry = new IdempotencyBodyTypeRegistry();

        bool resolved = registry.TryResolve(IdempotencyBodyTypeRegistry.GetKey(typeof(string)), out Type? type);

        resolved.Should().BeTrue();
        type.Should().Be(typeof(string));
    }

    [Fact]
    public void TryResolve_ReturnsFalse_ForUnregisteredType_WhenNoResolverIsSet()
    {
        IdempotencyBodyTypeRegistry registry = new IdempotencyBodyTypeRegistry();

        bool resolved = registry.TryResolve(IdempotencyBodyTypeRegistry.GetKey(typeof(MyResponseDto)), out Type? type);

        resolved.Should().BeFalse();
        type.Should().BeNull();
    }

    [Fact]
    public void TryResolve_ReturnsTrue_AfterExplicitAdd()
    {
        IdempotencyBodyTypeRegistry registry = new IdempotencyBodyTypeRegistry();
        registry.Add<MyResponseDto>();

        bool resolved = registry.TryResolve(IdempotencyBodyTypeRegistry.GetKey(typeof(MyResponseDto)), out Type? type);

        resolved.Should().BeTrue();
        type.Should().Be(typeof(MyResponseDto));
    }

    [Fact]
    public void TryResolve_UsesResolver_ForKeysNotInExplicitDictionary()
    {
        IdempotencyBodyTypeRegistry registry = new IdempotencyBodyTypeRegistry();
        registry.SetResolver(key => key == IdempotencyBodyTypeRegistry.GetKey(typeof(MyResponseDto))
            ? typeof(MyResponseDto)
            : null);

        bool resolved = registry.TryResolve(IdempotencyBodyTypeRegistry.GetKey(typeof(MyResponseDto)), out Type? type);

        resolved.Should().BeTrue();
        type.Should().Be(typeof(MyResponseDto));
    }

    [Fact]
    public void TryResolve_ReturnsFalse_WhenResolverReturnsNull()
    {
        IdempotencyBodyTypeRegistry registry = new IdempotencyBodyTypeRegistry();
        registry.SetResolver(key => null);

        bool resolved = registry.TryResolve(IdempotencyBodyTypeRegistry.GetKey(typeof(NotRegisteredDto)), out Type? type);

        resolved.Should().BeFalse();
        type.Should().BeNull();
    }

    [Fact]
    public void TryResolve_PrefersExplicitlyRegisteredType_OverResolver()
    {
        IdempotencyBodyTypeRegistry registry = new IdempotencyBodyTypeRegistry();
        registry.Add<MyResponseDto>();

        bool resolverCalled = false;
        registry.SetResolver(key =>
        {
            resolverCalled = true;
            return typeof(NotRegisteredDto);
        });

        bool resolved = registry.TryResolve(IdempotencyBodyTypeRegistry.GetKey(typeof(MyResponseDto)), out Type? type);

        resolved.Should().BeTrue();
        type.Should().Be(typeof(MyResponseDto));
        resolverCalled.Should().BeFalse();
    }

    [Fact]
    public void CreateAssemblyResolver_ResolvesType_FromExplicitlyTrustedAssembly()
    {
        Func<string, Type?> resolver = IdempotencyBodyTypeRegistry
            .CreateAssemblyResolver(typeof(MyResponseDto).Assembly);

        Type? resolved = resolver(typeof(MyResponseDto).FullName!);

        resolved.Should().Be(typeof(MyResponseDto));
    }

    [Fact]
    public void CreateAssemblyResolver_ReturnsNull_ForTypeNotInTrustedAssemblies()
    {
        Func<string, Type?> resolver = IdempotencyBodyTypeRegistry
            .CreateAssemblyResolver(typeof(MyResponseDto).Assembly);

        Type? resolved = resolver(typeof(string).FullName!);

        resolved.Should().BeNull();
    }

    [Fact]
    public void TryResolve_WithAssemblyResolver_ResolvesOwnDtoTypes_WithoutExplicitPerTypeRegistration()
    {
        IdempotencyBodyTypeRegistry registry = new IdempotencyBodyTypeRegistry();
        registry.SetResolver(IdempotencyBodyTypeRegistry.CreateAssemblyResolver(typeof(MyResponseDto).Assembly));

        bool resolved = registry.TryResolve(IdempotencyBodyTypeRegistry.GetKey(typeof(MyResponseDto)), out Type? type);

        resolved.Should().BeTrue();
        type.Should().Be(typeof(MyResponseDto));
    }
}
