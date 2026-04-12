# Stage 1: Build CXB frontend
FROM node:22-alpine AS cxb-build
RUN apk add --no-cache git
WORKDIR /cxb
COPY cxb/package.json cxb/yarn.lock* cxb/package-lock.json* ./
RUN yarn install || npm install
COPY cxb/ .
# Vite/Routify plugins run git commands during build; init a stub repo
# so they don't fail (the .git dir isn't copied into the container).
RUN git init && git config user.email "build@docker" && git config user.name "build" && git add -A && git commit -m "docker build" --allow-empty
RUN yarn build || npm run build

# Stage 2: Build C# AOT binary with embedded CXB
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk add --no-cache clang build-base zlib-dev zlib-static openssl-dev openssl-libs-static
WORKDIR /src
COPY . .
# Copy the freshly-built CXB dist into cxb/dist/client/ so the
# EmbeddedResource glob in dmart.csproj picks it up.
COPY --from=cxb-build /cxb/dist/client/ cxb/dist/client/
# Build a fully static binary — no runtime deps on libssl, libgssapi, libicu.
RUN dotnet publish dmart.csproj -r linux-musl-x64 \
      -p:PublishAot=true \
      -p:StripSymbols=true \
      -p:StaticExecutable=true \
      -c Release -o /out \
    && rm -f /out/*.dbg /out/*.pdb /out/*.Development.json /out/*.staticwebassets* /out/*.deps.json

# Stage 3: Minimal runtime image — no runtime libs needed (static binary)
FROM alpine:latest
COPY --from=build --chmod=755 /out /app
COPY --from=cxb-build --chmod=755 /cxb/dist/client/ /app/cxb/
WORKDIR /app
ENTRYPOINT ["./dmart"]
