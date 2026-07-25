<script lang="ts">
    import {Table, TableBody, TableBodyCell, TableBodyRow, TableHead, TableHeadCell} from "flowbite-svelte";

    // Values come straight from server-supplied entry records (displayname,
    // payload body, /info/* responses), so they are never injected as HTML.
    // Scalars go through ordinary `{...}` interpolation (auto-escaped) and
    // objects recurse through this same component in its `nested` form.
    export let entry: Record<string, any> = {};
    // `nested` swaps the table markup for the <ul> used to show object values,
    // so <svelte:self> can recurse without nesting whole tables.
    export let nested: boolean = false;
</script>

{#if nested}
  <ul class="mx-5">
    {#each Object.entries(entry) as [key, value]}
      <li>
        <b>{key}: </b>{#if value === null}N/A{:else if typeof value === "object"}<svelte:self entry={value} nested={true} />{:else}{value}{/if}
      </li>
    {/each}
  </ul>
{:else}
  <div class="h-full" style="overflow-y: auto;">
    <Table class="h-full" striped>
      <TableHead>
          <TableHeadCell>Key</TableHeadCell>
          <TableHeadCell>Value</TableHeadCell>
      </TableHead>
      <TableBody>
          {#each Object.keys(entry) as key}
            <TableBodyRow>
              <TableBodyCell>{key}</TableBodyCell>
              <TableBodyCell>
                {#if entry[key] === null}
                  N/A
                {:else if typeof entry[key] === "object"}
                  <svelte:self entry={entry[key]} nested={true} />
                {:else}
                  {entry[key]}
                {/if}
              </TableBodyCell>
            </TableBodyRow>
          {/each}
      </TableBody>
    </Table>
  </div>
{/if}
