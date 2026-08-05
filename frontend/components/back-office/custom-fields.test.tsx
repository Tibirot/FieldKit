// @vitest-environment jsdom

import { zodResolver } from "@hookform/resolvers/zod";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useForm } from "react-hook-form";
import { describe, expect, it } from "vitest";
import { z } from "zod";

import { CustomFields } from "@/components/back-office/custom-fields";
import type { FieldDefinition } from "@/lib/api/field-definitions";
import { customFieldSchema } from "@/lib/forms/custom-field-schema";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { render } from "@/test/render";

const define = (over: Partial<FieldDefinition>): FieldDefinition => ({
  id: crypto.randomUUID(),
  entity: "Outlet",
  key: "field",
  label: "Field",
  type: "Text",
  required: false,
  options: [],
  maxLength: null,
  minimum: null,
  maximum: null,
  ...over,
});

/**
 * A real form around the fields under test.
 *
 * The alternative — a mocked `control` — would assert that props were passed rather than that the
 * thing works. This wires the same resolver, the same schema and the same submit path the outlet
 * form uses, so a message appearing next to a control is the message a person would see.
 */
function Harness({
  definitions,
  values = {},
  onSubmit = () => {},
}: {
  definitions: FieldDefinition[];
  values?: Record<string, unknown>;
  onSubmit?: (values: Record<string, unknown>) => void;
}) {
  // The real catalogue here, unlike the schema's own tests: this file is about what a person
  // reads, so the words are part of what is under test.
  const messages = useValidationMessages();

  const form = useForm({
    resolver: zodResolver(z.object({ custom: customFieldSchema(definitions, messages) })),
    defaultValues: { custom: values },
    mode: "onBlur",
  });

  return (
    <form onSubmit={form.handleSubmit((parsed) => onSubmit(parsed.custom))} noValidate>
      <CustomFields
        definitions={definitions}
        control={form.control as never}
        errors={form.formState.errors}
      />
      <button type="submit">Save</button>
    </form>
  );
}

const control = (label: string) => screen.getByLabelText(new RegExp(label, "i"));
const save = () => userEvent.click(screen.getByRole("button", { name: "Save" }));

describe("<CustomFields>", () => {
  it("renders nothing at all when a tenant has defined nothing", () => {
    // Not an empty box with a heading. A tenant that declared no custom fields should see no
    // evidence the feature exists.
    const { container } = render(<Harness definitions={[]} />);

    expect(container.querySelector("fieldset")).toBeNull();
  });

  it("gives each type the control it needs", () => {
    render(
      <Harness
        definitions={[
          define({ key: "chillers", label: "Chillers", type: "Number", minimum: 0, maximum: 50 }),
          define({ key: "note", label: "Note", type: "Text", maxLength: 5 }),
          define({ key: "refit", label: "Refit", type: "Date" }),
          define({ key: "parking", label: "Has parking", type: "Boolean" }),
          define({ key: "ownership", label: "Ownership", type: "Choice", options: ["independent", "franchise"] }),
        ]}
      />,
    );

    const chillers = control("Chillers") as HTMLInputElement;
    expect(chillers.type).toBe("number");
    expect(chillers.min).toBe("0");
    expect(chillers.max).toBe("50");

    // "any", because a definition says nothing about precision and the default step of 1 would
    // silently refuse 12.5 for a field whose bounds allow it.
    expect(chillers.step).toBe("any");

    expect((control("Note") as HTMLInputElement).maxLength).toBe(5);
    expect((control("Refit") as HTMLInputElement).type).toBe("date");
    expect((control("Has parking") as HTMLInputElement).type).toBe("checkbox");

    const ownership = control("Ownership") as HTMLSelectElement;
    expect([...ownership.options].map((option) => option.value)).toEqual(["", "independent", "franchise"]);
  });

  it("puts the message beside the control that caused it", async () => {
    // The reason the schema exists at all — a list of sentences at the top of a long form leaves
    // someone hunting for which of eleven fields it is about.
    render(
      <Harness
        definitions={[define({ key: "chillers", label: "Chillers", type: "Number", maximum: 50 })]}
      />,
    );

    const chillers = control("Chillers");
    await userEvent.type(chillers, "900");
    await save();

    const message = await screen.findByText("Must be at most 50.");

    expect(chillers.getAttribute("aria-invalid")).toBe("true");
    expect(chillers.getAttribute("aria-describedby")).toBe(message.id);
  });

  it("says a required field the tenant left empty is required", async () => {
    render(
      <Harness
        definitions={[
          define({ key: "ownership", label: "Ownership", type: "Choice", options: ["independent"], required: true }),
        ]}
      />,
    );

    await save();

    // Not "Ownership is required" — the message renders under its own label, so repeating the
    // field name is redundant, and interpolating it produces ungrammatical Romanian.
    expect(await screen.findByText("This field is required.")).toBeTruthy();
  });

  it("marks a required field required, once", () => {
    // The asterisk is decorative and `aria-hidden`; `required` on the control is what is announced.
    // Both in the accessibility tree reads as "required required".
    render(<Harness definitions={[define({ key: "ownership", label: "Ownership", required: true })]} />);

    expect((control("Ownership") as HTMLInputElement).required).toBe(true);
    expect(screen.getByText("*").getAttribute("aria-hidden")).toBe("true");
  });

  it("never makes a boolean required, whatever the definition says", async () => {
    // `required` on a checkbox means "must be ticked", which is a different rule from the
    // catalogue's "must have an answer" — and no is an answer.
    const submitted: Record<string, unknown>[] = [];

    render(
      <Harness
        definitions={[define({ key: "parking", label: "Has parking", type: "Boolean", required: true })]}
        values={{ parking: false }}
        onSubmit={(values) => submitted.push(values)}
      />,
    );

    expect((control("Has parking") as HTMLInputElement).required).toBe(false);

    await save();

    expect(submitted).toEqual([{ parking: false }]);
  });

  it("submits a number as a number", async () => {
    const submitted: Record<string, unknown>[] = [];

    render(
      <Harness
        definitions={[define({ key: "chillers", label: "Chillers", type: "Number" })]}
        onSubmit={(values) => submitted.push(values)}
      />,
    );

    await userEvent.type(control("Chillers"), "3");
    await save();

    expect(submitted).toEqual([{ chillers: 3 }]);
  });

  it("submits an emptied field as absent, not as an empty string", async () => {
    // The same rule the CSV import follows: an optional choice left alone must not arrive as ""
    // and fail as "not one of the options". Null rather than undefined because that is what RHF can
    // carry — and the API reads a JSON null as absent, so the two mean the same thing to the server.
    const submitted: Record<string, unknown>[] = [];

    render(
      <Harness
        definitions={[define({ key: "note", label: "Note", type: "Text" })]}
        values={{ note: "x" }}
        onSubmit={(values) => submitted.push(values)}
      />,
    );

    await userEvent.clear(control("Note"));
    await save();

    expect(submitted).toEqual([{ note: null }]);
  });

  it("shows what is already stored", () => {
    render(
      <Harness
        definitions={[
          define({ key: "chillers", label: "Chillers", type: "Number" }),
          define({ key: "parking", label: "Has parking", type: "Boolean" }),
        ]}
        values={{ chillers: 4, parking: true }}
      />,
    );

    expect((control("Chillers") as HTMLInputElement).value).toBe("4");
    expect((control("Has parking") as HTMLInputElement).checked).toBe(true);
  });
});
