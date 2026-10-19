import { emptyLayout, FormRenderer, type RendererField } from "@enterpriseforms/form-renderer";
import { Badge, Button, Card, Checkbox, DataTable, Dialog, EmptyState, LoadingBlock, PageHeader, ProgressBar, Select, StatusMessage, Tabs, TextArea, TextInput } from "@enterpriseforms/ui";
import { useState } from "react";

const ids = { name: "0199a2b3-c4d5-7e6f-8123-456789abcde1", age: "0199a2b3-c4d5-7e6f-8123-456789abcde2", dob: "0199a2b3-c4d5-7e6f-8123-456789abcde3", custody: "0199a2b3-c4d5-7e6f-8123-456789abcde4", status: "0199a2b3-c4d5-7e6f-8123-456789abcde5" };
const galleryFields: RendererField[] = [
  { fieldDefinitionId: ids.name, fieldKey: "given_name", fieldDisplayName: "Given name", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.age, fieldKey: "age", fieldDisplayName: "Age", fieldDataType: "Integer", options: [] },
  { fieldDefinitionId: ids.dob, fieldKey: "date_of_birth", fieldDisplayName: "Date of birth", fieldDataType: "Date", options: [] },
  { fieldDefinitionId: ids.custody, fieldKey: "in_custody", fieldDisplayName: "In custody", fieldDataType: "Boolean", options: [] },
  { fieldDefinitionId: ids.status, fieldKey: "status", fieldDisplayName: "Status", fieldDataType: "Option", options: [{ fieldOptionId: "a", optionKey: "active", optionDisplayText: "Active", optionSortOrder: 1, isActive: true }, { fieldOptionId: "b", optionKey: "closed", optionDisplayText: "Closed", optionSortOrder: 2, isActive: true }] },
];
const galleryLayout = {
  ...emptyLayout("Gallery form"),
  sections: [
    {
      sectionKey: "main",
      title: "Four-column grid sample",
      gridColumns: 4 as const,
      elements: [
        { kind: "field" as const, fieldDefinitionId: ids.name, control: "text" as const, label: "Given name", required: true, layout: { columnSpan: 2 as const, startOnNewRow: false }, validation: { maxLength: 100 } },
        { kind: "field" as const, fieldDefinitionId: ids.age, control: "number" as const, label: "Age", required: false, layout: { columnSpan: 1 as const, startOnNewRow: false } },
        { kind: "field" as const, fieldDefinitionId: ids.dob, control: "date" as const, label: "Date of birth", required: false, layout: { columnSpan: 1 as const, startOnNewRow: false } },
        { kind: "field" as const, fieldDefinitionId: ids.status, control: "select" as const, label: "Status", required: false, layout: { columnSpan: 2 as const, startOnNewRow: true } },
        { kind: "field" as const, fieldDefinitionId: ids.custody, control: "checkbox" as const, label: "In custody", required: false, layout: { columnSpan: 2 as const, startOnNewRow: false } },
      ],
    },
  ],
};

/** PG-069 internal component gallery: every control state with synthetic data only (ADR-PB-014). */
export function GalleryPage() {
  const [checked, setChecked] = useState(true);
  const [select, setSelect] = useState<string | undefined>("active");
  const [dialog, setDialog] = useState(false);
  const [values, setValues] = useState<Record<string, string>>({ given_name: "Avery", status: "active", in_custody: "true" });
  return (
    <>
      <PageHeader title="Component gallery" lead="Internal design-system validation page (PG-069). All data on this page is synthetic." />
      <StatusMessage tone="info" title="Synthetic content">
        Nothing here is loaded from the API; the gallery exists so keyboard, focus, zoom and contrast behaviour can be reviewed in isolation.
      </StatusMessage>
      <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card title="Buttons">
          <div className="flex flex-wrap gap-2">
            <Button>Primary</Button>
            <Button variant="secondary">Secondary</Button>
            <Button variant="danger">Danger</Button>
            <Button variant="ghost">Ghost</Button>
            <Button disabled>Disabled</Button>
            <Button busy>Busy</Button>
            <Button size="sm">Small</Button>
          </div>
        </Card>
        <Card title="Status and badges">
          <div className="flex flex-col gap-2">
            <StatusMessage tone="success">Saved.</StatusMessage>
            <StatusMessage tone="warning">Review before publishing.</StatusMessage>
            <StatusMessage tone="danger">The request failed.</StatusMessage>
            <div className="flex flex-wrap gap-2">
              <Badge tone="success">Ready</Badge>
              <Badge tone="warning">Scanning</Badge>
              <Badge tone="danger">Rejected</Badge>
              <Badge tone="neutral">Draft</Badge>
            </div>
          </div>
        </Card>
        <Card title="Form controls">
          <div className="flex flex-col gap-3">
            <TextInput label="Text input" helpText="Help text sits under the label." defaultValue="Sample" />
            <TextInput label="Invalid input" error="This value is required" defaultValue="" required />
            <TextInput label="Disabled input" disabled defaultValue="Read only" />
            <TextArea label="Text area" defaultValue="Multi-line sample text." />
            <Select label="Select" value={select} onValueChange={setSelect} options={[{ value: "active", label: "Active" }, { value: "closed", label: "Closed" }, { value: "retired", label: "Retired (no longer available)", disabled: true }]} />
            <Checkbox label="Checked state is a black fill with a white check mark" checked={checked} onCheckedChange={setChecked} />
            <Checkbox label="Disabled checkbox" checked={false} onCheckedChange={() => undefined} disabled />
          </div>
        </Card>
        <Card title="Progress, loading, empty">
          <ProgressBar label="warrant.pdf" value={62} />
          <LoadingBlock label="Loading records" />
          <EmptyState title="No records yet" actions={<Button variant="secondary">Create one</Button>}>
            Create a record from a published form.
          </EmptyState>
        </Card>
        <Card title="Dialog and tabs" className="lg:col-span-2">
          <Button variant="secondary" onClick={() => setDialog(true)}>
            Open dialog
          </Button>
          <Dialog open={dialog} onOpenChange={setDialog} title="Confirm action" description="Focus is trapped; Escape closes." footer={<Button onClick={() => setDialog(false)}>Close</Button>}>
            <p>Dialog content.</p>
          </Dialog>
          <Tabs label="Gallery tabs" items={[{ value: "one", label: "First", content: <p>First panel</p> }, { value: "two", label: "Second", content: <p>Second panel</p> }]} />
        </Card>
        <Card title="Table" className="lg:col-span-2">
          <DataTable caption="Sample rows" rowKey={(row) => row.id} rows={[{ id: "1", name: "Avery Lee", status: "Ready" }, { id: "2", name: "Jordan Kim", status: "Scanning" }]} columns={[{ key: "name", header: "Name", isRowHeader: true, render: (row) => row.name }, { key: "status", header: "Status", render: (row) => <Badge tone={row.status === "Ready" ? "success" : "warning"}>{row.status}</Badge> }]} />
        </Card>
        <Card title="Form renderer (responsive 4/2/1)" className="lg:col-span-2">
          <FormRenderer layout={galleryLayout} fields={galleryFields} values={values} onChange={(key, value) => setValues((current) => ({ ...current, [key]: value }))} />
        </Card>
      </div>
    </>
  );
}
