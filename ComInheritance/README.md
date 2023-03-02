# COM Interface Generator

The purpose of this generator is to allow using .NET inheritance when declaring COM interfaces.

The .NET runtime does not support inheritance when creating COM wrappers, only declared members are considered.
As a result, all inherited members must be declared directly on the .NET interface.
As new and new versions of COM interfaces are introduced, keeping all the members in sync becomes tedious and error prone. 

With the COM Interface Generator, members need to be declared only once and a flattened interface with all members will be generated.

Since this task cannot be solved using partial types and generators cannot hide user types, the convention is that the user type's name will start with an underscore and that it will be private. 
A corresponding flattened type will be generated as public and without an underscore.

The generator collects only interfaces with:
1. `[ComImport]`
2. `[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]`
3. name starting with an underscore

## Usage

Example input:
```C#
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInterface 
{ 
    void A(); 
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private interface _IInterface1 : IInterface 
{ 
    void B(); 
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private interface _IInterface2 : _IInetrface1 
{
    void C(); 
}
```

Generated output:
```C#
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInterface1 
{
    #region IInterface
    new void A();
    #endregion

    void B();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInterface2 
{
    #region IInterface1
    #region IInterface
    new void A();
    #endregion

    new void B();
    #endregion

    void C();
}
```

All trivia including XML documentation is preserved.

<details open>
    <summary>Why not partial?</summary>

###

One would be tempted to use `partial` for this purpose and indeed, this was the original design of the generator, avoiding the extra private declarations.

However, for this to work it is critical to ensure that the generated interface would be compiled before the user interface, so that the inherited members will be compiled before the newly declared members.
This would require the source generator to add the source tree at the start of a compilation, which is not supported.

Moreover, the specification explicitly states (ECMA 334, 6th ed, §14.3.1 Class members) that the ordering of members in partial types is undefined.

The source code can still generate the `partial` idea if `GENERATE_FULL_TYPES` is undefined.
