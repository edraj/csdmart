#!/bin/bash

Dmart__PostgresConnection='Host=localhost;Username=dmart;Password=tramd;Database=dmart' \
Dmart_AdminShortname='cstest' \
Dmart_AdminPassword='ctest-password-123' \
ASPNETCORE_URLS=http://127.0.0.1:5099 \
./bin/Release/net10.0/linux-x64/publish/dmart



