#pragma once

#include "../utils/json.h"

namespace KitSettings
{
    class Settings
    {
    public:
        Settings(
            const HINSTANCE hinstance, // Module handle of the PowerToy DLL 'IMAGE_DOS_HEADER __ImageBase'
            std::wstring_view powertoy_name);

        // Add additional general information to the PowerToy settings.
        void set_description(UINT resource_id);
        void set_description(std::wstring_view description);

        void set_icon_key(std::wstring_view icon_key);
        void set_overview_link(std::wstring_view overview_link);
        void set_video_link(std::wstring_view video_link);

        // Add properties to the PowerToy settings.
        void add_bool_toggle(std::wstring_view name, UINT description_resource_id, bool value);
        void add_bool_toggle(std::wstring_view name, std::wstring_view description, bool value);

        void add_int_spinner(std::wstring_view name, UINT description_resource_id, int value, int min, int max, int step);
        void add_int_spinner(std::wstring_view name, std::wstring_view description, int value, int min, int max, int step);

        void add_string(std::wstring_view name, UINT description_resource_id, std::wstring_view value);
        void add_string(std::wstring_view name, std::wstring_view description, std::wstring_view value);

        void add_multiline_string(std::wstring_view name, UINT description_resource_id, std::wstring_view value);
        void add_multiline_string(std::wstring_view name, std::wstring_view description, std::wstring_view value);

        void add_color_picker(std::wstring_view name, UINT description_resource_id, std::wstring_view value);
        void add_color_picker(std::wstring_view name, std::wstring_view description, std::wstring_view value);

        void add_choice_group(std::wstring_view name, UINT description_resource_id, std::wstring_view value, const std::vector<std::pair<std::wstring, UINT>>& keys_and_text_ids);
        void add_choice_group(std::wstring_view name, std::wstring_view description, std::wstring_view value, const std::vector<std::pair<std::wstring, std::wstring>>& keys_and_texts);

        void add_dropdown(std::wstring_view name, UINT description_resource_id, std::wstring_view value, const std::vector<std::pair<std::wstring, UINT>>& keys_and_text_ids);
        void add_dropdown(std::wstring_view name, std::wstring_view description, std::wstring_view value, const std::vector<std::pair<std::wstring, std::wstring>>& keys_and_texts);

        void add_custom_action(std::wstring_view name, UINT description_resource_id, UINT button_text_resource_id, UINT ext_description_resource_id);
        void add_custom_action(std::wstring_view name, UINT description_resource_id, UINT button_text_resource_id, std::wstring_view value);
        void add_custom_action(std::wstring_view name, std::wstring_view description, std::wstring_view button_text, std::wstring_view value);

        void add_header_szLarge(std::wstring_view name, std::wstring_view description, std::wstring_view value);
        // Serialize the internal json to a string.
        std::wstring serialize();
        // Serialize the internal json to the input buffer.
        bool serialize_to_buffer(wchar_t* buffer, int* buffer_size);

    private:
        json::JsonObject m_json;
        int m_curr_priority = 0; // For keeping order when adding elements.
        HINSTANCE m_instance;

        std::wstring get_resource(UINT resource_id);
    };

    class PowerToyValues
    {
    public:
        PowerToyValues(std::wstring_view powertoy_name, std::wstring_view powertoy_key);
        static PowerToyValues from_json_string(std::wstring_view json, std::wstring_view powertoy_key);
        static PowerToyValues load_from_settings_file(std::wstring_view powertoy_key);

        template<typename T>
        inline void add_property(std::wstring_view name, T value)
        {
            json::JsonObject prop_value;
            prop_value.SetNamedValue(L"value", json::value(value));
            m_json.GetNamedObject(L"properties").SetNamedValue(name, prop_value);
        }

        std::optional<bool> get_bool_value(std::wstring_view property_name) const;
        std::optional<int> get_int_value(std::wstring_view property_name) const;
        std::optional<unsigned int> get_uint_value(std::wstring_view property_name) const;
        std::optional<std::wstring> get_string_value(std::wstring_view property_name) const;
        std::optional<json::JsonObject> get_json(std::wstring_view property_name) const;
        json::JsonObject get_raw_json();

        std::wstring serialize();
        void save_to_settings_file();

    private:
        const std::wstring m_version = L"1.0";
        void set_version();
        json::JsonObject m_json;
        std::wstring _key;
        PowerToyValues() {}
    };

    class CustomActionObject
    {
    public:
        static CustomActionObject from_json_string(std::wstring_view json)
        {
            return CustomActionObject(json::JsonValue::Parse(json).GetObjectW());
        }

        std::wstring get_name() { return m_json.GetNamedString(L"action_name").c_str(); }
        std::wstring get_value() { return m_json.GetNamedString(L"value").c_str(); }

    protected:
        CustomActionObject(json::JsonObject action_json) :
            m_json(std::move(action_json)){};
        json::JsonObject m_json;
    };

}

// Imported module sources may still use the upstream namespace.
namespace PowerToysSettings = KitSettings;
