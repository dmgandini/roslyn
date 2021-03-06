﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.VisualStudio.Shell.Interop

Friend Class MockVbi
    Inherits VisualBasicCompiler

    Public Sub New(responseFile As String, baseDirectory As String, args As String())
        MyBase.New(VisualBasicCommandLineParser.Interactive, responseFile, args, baseDirectory, Nothing, IO.Path.GetTempPath())

    End Sub

    Protected Overrides Sub CompilerSpecificSqm(sqm As IVsSqmMulti, sqmSession As UInteger)
        Throw New NotImplementedException()
    End Sub

    Protected Overrides Function GetSqmAppID() As UInteger
        Throw New NotImplementedException()
    End Function
End Class
