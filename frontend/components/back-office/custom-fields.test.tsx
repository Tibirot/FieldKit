// @vitest-environment jsdom

import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { CustomFields } from "@/components/back-office/custom-fields";
import type { FieldDefinition } from "@/lib/api/field-definitions";
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

/** The control a definition produced, found the way a person would — by its label. */
const control = (label: string) => screen.getByLabelText(new RegExp(label, "i"));

describe("<CustomFields>", () => {
  it("renders nothing at all when a tenant has defined nothing", () => {
    // Not an empty box with a heading. A tenant that declared no custom fields should see no
    // evidence the feature exists.
    const { container } = render(<CustomFields definitions={[]} values={{}} onChange={vi.fn()} />);

    expect(container.textContent).toBe("");
  });

  it("hands each definition's own constraint to the browser", async () => {
    // The whole config-driven claim, checked as attributes: nothing here knows what a chiller count
    // is, and the browser will refuse 900 before a request is made.
    render(
      <CustomFields
        definitions={[
          define({ key: "chillers", label: "Chillers", type: "Number", minimum: 0, maximum: 50 }),
          define({ key: "note", label: "Note", type: "Text", maxLength: 5 }),
          define({ key: "refit", label: "Refit", type: "Date" }),
        ]}
        values={{}}
        onChange={vi.fn()}
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
  });

  it("turns a choice into exactly its options, plus a way to pick none", () => {
    render(
      <CustomFields
        definitions={[
          define({ key: "ownership", label: "Ownership", type: "Choice", options: ["independent", "franchise"] }),
        ]}
        values={{}}
        onChange={vi.fn()}
      />,
    );

    const select = control("Ownership") as HTMLSelectElement;
    const values = [...select.options].map((option) => option.value);

    expect(values).toEqual(["", "independent", "franchise"]);
  });

  it("marks a required field required, once", () => {
    // The asterisk is decorative and `aria-hidden`; `required` on the control is what is announced
    // and what the browser enforces. Both in the accessibility tree reads as "required required".
    render(
      <CustomFields
        definitions={[define({ key: "ownership", label: "Ownership", required: true })]}
        values={{}}
        onChange={vi.fn()}
      />,
    );

    expect((control("Ownership") as HTMLInputElement).required).toBe(true);
    expect(screen.getByText("*").getAttribute("aria-hidden")).toBe("true");
  });

  it("never makes a boolean required, whatever the definition says", async () => {
    // `required` on a checkbox means "must be ticked", which is a different rule from the
    // catalogue's "must have an answer" — and false is an answer.
    render(
      <CustomFields
        definitions={[define({ key: "parking", label: "Has parking", type: "Boolean", required: true })]}
        values={{}}
        onChange={vi.fn()}
      />,
    );

    expect((control("Has parking") as HTMLInputElement).required).toBe(false);
  });

  it("reports a number as a number", async () => {
    const onChange = vi.fn();

    render(
      <CustomFields
        definitions={[define({ key: "chillers", label: "Chillers", type: "Number" })]}
        values={{}}
        onChange={onChange}
      />,
    );

    await userEvent.type(control("Chillers"), "3");

    expect(onChange).toHaveBeenLastCalledWith("chillers", 3);
  });

  it("reports an emptied field as absent, not as an empty string", async () => {
    // The same rule the CSV import follows: an optional choice left alone must not arrive as ""
    // and fail as "not one of the options".
    const onChange = vi.fn();

    render(
      <CustomFields
        definitions={[define({ key: "note", label: "Note", type: "Text" })]}
        values={{ note: "x" }}
        onChange={onChange}
      />,
    );

    await userEvent.clear(control("Note"));

    expect(onChange).toHaveBeenLastCalledWith("note", undefined);
  });

  it("shows what is already stored", () => {
    render(
      <CustomFields
        definitions={[
          define({ key: "chillers", label: "Chillers", type: "Number" }),
          define({ key: "parking", label: "Has parking", type: "Boolean" }),
        ]}
        values={{ chillers: 4, parking: true }}
        onChange={vi.fn()}
      />,
    );

    expect((control("Chillers") as HTMLInputElement).value).toBe("4");
    expect((control("Has parking") as HTMLInputElement).checked).toBe(true);
  });
});
