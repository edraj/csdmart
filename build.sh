#!/bin/bash
dotnet clean
rm -rf bin obj
dotnet publish -r linux-x64 -p:PublishAot=true -c Release
